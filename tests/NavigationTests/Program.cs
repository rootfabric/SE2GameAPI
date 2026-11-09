using System.Numerics;
using SE2GameAPI.Game.Navigation;
void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
foreach(var target in new[]{new Point(30,0,0),new Point(-30,0,0),new Point(0,30,0),new Point(0,-30,0),new Point(0,0,30),new Point(0,0,-30),new Point(12,17,-21)})
{
    Point position=default,velocity=new(3,-2,1);
    for(int i=0;i<2400;i++)
    {
        var acceleration=NavigationMath.Flight(target-position,velocity,5,2,1);
        Check(acceleration.Length<=2.00001,"Acceleration limit");
        velocity+=acceleration*.05;position+=velocity*.05;
    }
    Check((target-position).Length<1 && velocity.Length<.25,"Flight did not settle");
}
foreach(var forward in new[]{new Point(0,0,-1),new Point(0,0,1),new Point(1,0,0),new Point(-1,0,0)})
{
    var desired=NavigationMath.Orientation(forward,new(0,1,0)); var current=Quaternion.Identity;
    for(int i=0;i<2000;i++)
    {
        var (rate,angle)=NavigationMath.Rotation(current,desired);
        if(rate.Length>1e-9) current=Quaternion.Normalize(current*Quaternion.CreateFromAxisAngle(rate.Unit.Float,(float)(rate.Length*.05)));
    }
    Check(NavigationMath.Rotation(current,desired).Angle<.01,"Orientation, including 180 degrees");
}
var left=NavigationMath.Ground(new(-10,0,-10),new(0,0,-1),new(0,1,0),0,4,1);
var right=NavigationMath.Ground(new(10,0,-10),new(0,0,-1),new(0,1,0),0,4,1);
Check(left.Steering<0 && right.Steering>0,"Steering signs");
Check(NavigationMath.Ground(new(0,300,-10),new(0,0,-1),new(0,1,0),0,4,1).Distance==10,"Ground plane projection");
Check(NavigationMath.Ground(default,new(0,0,-1),new(0,1,0),3,4,1).Throttle<0,"Ground braking");
// Bicycle-model validation; not a replacement for a live wheeled SE2 grid.
foreach(var target in new[]{new Point(-20,0,-30),new Point(20,0,-30),new Point(10,0,20)})
{
    Point position=default; double yaw=0,speed=0; bool arrived=false;
    for(int i=0;i<6000;i++)
    {
        var forward=new Point(-Math.Sin(yaw),0,-Math.Cos(yaw));
        var control=NavigationMath.Ground(target-position,forward,new(0,1,0),speed,3,1);
        speed+=control.Throttle*3*.05;
        yaw+=-control.Steering*speed/2.5*.05;
        position+=forward*speed*.05;
        if(control.Distance<=1 && Math.Abs(speed)<.25) { arrived=true;break; }
    }
    Check(arrived,"Ground kinematic model did not reach target");
}
try { NavigationMath.Parse([double.NaN,0,0],"target"); throw new Exception("NaN accepted"); } catch(ArgumentException) { }
Console.WriteLine("PASS: six-axis flight, braking, rotated heading, 180-degree turn, ground steering/projection/braking, NaN rejection");
