using System.Diagnostics;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.DCS.CoreData;
using Keen.VRage.Physics.Data;
using SE2GameAPI.Game.Navigation;
using SE2GameAPI.Redis;
using V = Keen.VRage.Library.Mathematics.Vector3;
using Q = System.Numerics.Quaternion;
namespace SE2GameAPI.Game.Devices;

internal sealed class AutopilotDevice(CubeGridComponent grid) : DeviceAdapter
{
    private readonly DEntity entityId = grid.DEntity;
    private readonly List<SeatComponent> seats = new();
    private readonly List<WheelCornerModuleComponent> wheels = new();
    public override string Id { get; } = "autopilot:" + grid.DEntity;
    public bool Ready;
    public Func<bool> CanStart = () => true;
    public bool Active => request != null;
    private NavigationRequest? request;
    private NavigationRequest? mission;
    private Point target;
    private Q orientation;
    private Point orientationUp;
    private long started, previousTick, revision = 1;
    private int version;
    private ControlReferenceOrientation originalReference;
    private string status = "Idle", missionId = "", error = "";
    private double distance, headingError;
    private double settled;
    private static readonly string[] capabilities = ["navigation.flight", "navigation.ground.experimental", "navigation.stop"];
    private sealed record NavigationState(string MissionId, string Mode, string Error, double[] Position,
        double[] Velocity, double[] Forward, double[] Up, double Distance, double HeadingErrorDeg,
        double[] PositiveThrust, double[] NegativeThrust, double Mass, int Wheels, bool Ready,
        double[]? Target, bool CollisionAvoidance = false);

    public void ResetBlocks() { Ready = false; seats.Clear(); wheels.Clear(); }
    public void AddBlock(CubeBlockComponent block)
    {
        var seat = block.Entity.TryGet<SeatComponent>(); if (seat != null) seats.Add(seat);
        var wheel = block.Entity.TryGet<WheelCornerModuleComponent>(); if (wheel != null) wheels.Add(wheel);
    }
    private bool Alive(Session session) => session.Scene.IsEntityAlive(entityId) && !grid.GridClosing;
    private static Point P(V v) => new(v.X,v.Y,v.Z);
    private static V Vec(Point p) => new((float)p.X,(float)p.Y,(float)p.Z);
    private Q Rotation()
    {
        var q = grid.Data.GetWorldTransform().Orientation;
        return new(q.X,q.Y,q.Z,q.W);
    }
    private Point Position()
    {
        var p = grid.Data.GetWorldTransform().Position; return new(p.X,p.Y,p.Z);
    }
    private bool HasPilot() => seats.Any(s => s.HasPilot);
    public override DeviceState? Read(Session session)
    {
        if (!Alive(session)) return null;
        grid.Data.TryGet<RigidBodyData>(out var body);
        grid.Data.TryGet<RigidBodyMassProperties>(out var mass);
        grid.Data.TryGet<MaxThrustData>(out var thrust);
        var q=Rotation();
        var properties=new NavigationState(missionId,mission?.Mode ?? "none",error,Position().Array,P(body.LinearVelocity).Array,
            Point.From(System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ,q)).Array,
            Point.From(System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY,q)).Array,
            distance,headingError*180/Math.PI,P(thrust.Regular.Positive).Array,P(thrust.Regular.Negative).Array,
            mass.Mass,wheels.Count,Ready,mission?.Target);
        return new(Id,entityId.ToString(),grid.DisplayName.EvaluateText(),"Grid Autopilot","autopilot",
            Active,Active,Ready,revision,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),status,capabilities,properties);
    }
    public override Func<DeviceState,bool>? Apply(DeviceCommand command, DeviceState before)
    {
        if (command.Cmd is "autopilot_stop" or "stop")
        {
            Stop("Stopped",""); return actual => actual.Status == "Stopped";
        }
        if (command.Cmd != "autopilot_goto") return null;
        if (Active) throw new InvalidOperationException("autopilot_busy_stop_first");
        if (!CanStart()) throw new InvalidOperationException("autopilot_capacity");
        var value=command.Navigation ?? throw new ArgumentException("navigation_required");
        var destination=NavigationMath.Parse(value.Target,"target");
        if (!Ready || !grid.Data.Has<RigidBodyData>() || !grid.Data.Has<RigidBodyMassProperties>()) throw new InvalidOperationException("grid_not_ready");
        var physics=grid.Entity.TryGet<GridPhysicsComponent>();
        if (physics == null || physics.GridMotionType.ToString() != "Dynamic") throw new InvalidOperationException("grid_is_static");
        // By default a seated player does not cancel the mission; opt back in with KeepControlOnPlayer=false.
        if (!value.KeepControlOnPlayer && HasPilot()) throw new InvalidOperationException("player_has_control");
        if (value.Mode is not ("flight" or "ground")) throw new ArgumentException("invalid_mode");
        if (!double.IsFinite(value.MaxSpeed) || value.MaxSpeed is < .1 or > 80 ||
            !double.IsFinite(value.PositionTolerance) || value.PositionTolerance is < .05 or > 20 ||
            !double.IsFinite(value.HeadingToleranceDeg) || value.HeadingToleranceDeg is < .5 or > 30 ||
            !double.IsFinite(value.TimeoutSeconds) || value.TimeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("invalid_navigation_limits");
        if ((destination-Position()).Length>60000) throw new ArgumentException("target_too_far");
        if (value.Mode == "ground" && (value.MaxSpeed>10 || (destination-Position()).Length>1000))
            throw new ArgumentException("ground_limit_10mps_1000m");
        if (grid.Data.TryGet<OverriddenThrustData>(out var overridden) && P(overridden.DirectionalThrust).Length>1)
            throw new InvalidOperationException("manual_thrust_override_active");
        if (value.Mode == "flight")
        {
            if (!grid.Data.TryGet<MaxThrustData>(out var thrust) ||
                new[]{thrust.Regular.Positive.X,thrust.Regular.Positive.Y,thrust.Regular.Positive.Z,
                    thrust.Regular.Negative.X,thrust.Regular.Negative.Y,thrust.Regular.Negative.Z}.Any(v=>v<=0))
                throw new InvalidOperationException("six_direction_thrust_required");
            if (!grid.Data.TryGet<MaxTorqueData>(out var torque) || torque.MaxTorque<=0) throw new InvalidOperationException("no_working_gyroscopes");
        }
        else if (wheels.Count < 2 || wheels.Count > 16) throw new InvalidOperationException("ground_requires_2_to_16_wheels");
        var current=Rotation();
        if (value.AlignCourse && value.Forward != null) throw new ArgumentException("course_conflicts_with_forward");
        if (value.AlignCourse && value.Mode == "ground") throw new ArgumentException("ground_align_course_not_supported");
        var forward=value.Forward == null ? Point.From(System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ,current)) : NavigationMath.Parse(value.Forward,"forward");
        var up=value.Up == null ? Point.From(System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY,current)) : NavigationMath.Parse(value.Up,"up");
        var desiredOrientation=NavigationMath.Orientation(forward,up);
        // Ground heading needs space for a turn; this first ground controller arrives along its path.
        if (value.Mode == "ground" && value.Forward != null) throw new InvalidOperationException("ground_final_heading_not_supported");
        target=destination; orientation=desiredOrientation; orientationUp=up; request=mission=value; missionId=command.RequestId;
        version=grid.CubeBlockStorage.Version;
        if (!grid.Data.TryGet<ControlReferenceOrientation>(out originalReference))
            originalReference = new() { Orientation = Keen.VRage.Library.Mathematics.Quaternion.Identity };
        started=previousTick=Stopwatch.GetTimestamp(); settled=0; status="Navigating"; error=""; revision++;
        // Script-priority mode: an optionally ejected pilot can no longer override ControlData.
        if (value.EjectPilot)
            foreach (var seat in seats.Where(s => s.HasPilot))
                try { _ = seat.TryClearPilotAsync(); } catch { }
        // Stop existing aiming data so the angular velocity controller owns gyroscopes.
        grid.Data.TryRemove<TargetControlData>();
        grid.Data.Set(new ControlReferenceOrientation { Orientation = Keen.VRage.Library.Mathematics.Quaternion.Identity });
        return actual => actual.Status is "Navigating" or "Arrived";
    }
    public void Tick(Session session)
    {
        if (!Active) return;
        if (!Alive(session)) { request=null; status="Failed"; error="grid_removed"; return; }
        try
        {
            long now=Stopwatch.GetTimestamp(); double dt=Stopwatch.GetElapsedTime(previousTick,now).TotalSeconds; previousTick=now;
            if (dt>.75) { Stop("Failed","controller_gap"); return; }
            if (Stopwatch.GetElapsedTime(started,now).TotalSeconds>request!.TimeoutSeconds) { Stop("Failed","mission_timeout"); return; }
            if (grid.CubeBlockStorage.Version!=version) { Stop("Failed","grid_changed"); return; }
            // Player in a seat keeps the mission running by default; the controller keeps commanding.
            if (HasPilot() && !request!.KeepControlOnPlayer) { Stop("Cancelled","player_has_control"); return; }
            // Script-priority mode: a re-seated pilot is ejected again so their zero input
            // cannot overwrite the controller's ControlData.
            if (HasPilot() && request!.EjectPilot)
                foreach (var seat in seats.Where(s => s.HasPilot))
                    try { _ = seat.TryClearPilotAsync(); } catch { }
            var body=grid.Data.Get<RigidBodyData>(); var mass=grid.Data.Get<RigidBodyMassProperties>();
            var velocity=P(body.LinearVelocity); var delta=target-Position(); var q=Rotation();
            distance=delta.Length;
            // Course alignment: keep the nose pointed along the direction of travel to the target.
            // Close to the target the course vector degenerates, so hold the last good orientation.
            if (request.AlignCourse && distance>Math.Max(request.PositionTolerance,1e-3))
                orientation=NavigationMath.Orientation(delta.Unit,orientationUp);
            var rotation=NavigationMath.Rotation(q,orientation); headingError=rotation.Angle;
            if (request.Mode == "flight")
            {
                var thrust=grid.Data.Get<MaxThrustData>();
                var pos=P(thrust.Regular.Positive); var neg=P(thrust.Regular.Negative);
                double weakest=Math.Min(Math.Min(pos.X,Math.Min(pos.Y,pos.Z)),Math.Min(neg.X,Math.Min(neg.Y,neg.Z)))/mass.Mass;
                if (weakest<=.01) { Stop("Failed","insufficient_thrust"); return; }
                var acceleration=NavigationMath.Flight(delta,velocity,request.MaxSpeed,Math.Min(3,weakest*.4),request.PositionTolerance);
                var local=Point.From(System.Numerics.Vector3.Transform(acceleration.Float,Q.Conjugate(q)))*mass.Mass;
                double Axis(double force,double positive,double negative)=>Math.Clamp(force/(force>=0?positive:negative),-1,1);
                grid.Data.Set(new ControlData { Movement = new V((float)Axis(local.X,pos.X,neg.X),(float)Axis(local.Y,pos.Y,neg.Y),(float)Axis(local.Z,pos.Z,neg.Z)) });
                grid.Data.Set(new AngularControlData { TargetAngularVelocity=Vec(rotation.LocalRate) });
            }
            else
            {
                var forward=Point.From(System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ,q));
                var up=Point.From(System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY,orientation));
                var actualUp=Point.From(System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY,q));
                if(Point.Dot(actualUp,up)<.5) { Stop("Failed","ground_excessive_tilt"); return; }
                var ground=NavigationMath.Ground(delta,forward,up,Point.Dot(velocity,forward),request.MaxSpeed,request.PositionTolerance);
                distance=ground.Distance; headingError=ground.Heading;
                foreach(var wheel in wheels) if(wheel.IsParked) ((IParkingBrake)wheel).RequestToggle();
                grid.Data.Set(new ControlData { Movement = new V((float)ground.Steering,0,(float)-ground.Throttle) });
            }
            bool reached=distance<=request.PositionTolerance && velocity.Length<=.25 &&
                (request.Mode=="ground" || headingError<=request.HeadingToleranceDeg*Math.PI/180);
            settled=reached?settled+dt:0;
            if (settled>=.5) Stop("Arrived","");
        }
        catch(Exception e) { Stop("Failed",e.GetType().Name+": "+e.Message); }
    }
    public void Shutdown(Session session) { if(Active && Alive(session)) Stop("Cancelled","plugin_shutdown"); }
    private void Stop(string result,string reason)
    {
        if (result=="Stopped")
            try { System.IO.File.AppendAllText(@"C:\SE2GameAPI\stop_trace.log",
                DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff")+" Stop(Stopped)\n"+Environment.StackTrace+"\n---\n"); }
            catch { }
        if (Active)
        {
            grid.Data.Set(default(ControlData));
            grid.Data.Set(new AngularControlData { TargetAngularVelocity=V.Zero });
            grid.Data.Set(originalReference);
            // On termination use native dampening, never leave sustained thrust behind.
            grid.Data.Set(default(DampeningData));
            if (request!.Mode=="ground") foreach(var wheel in wheels) if(!wheel.IsParked) ((IParkingBrake)wheel).RequestToggle();
        }
        request=null; status=result; error=reason; revision++;
    }
}

