using System.Numerics;
namespace SE2GameAPI.Game.Navigation;

internal readonly record struct Point(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(X*X + Y*Y + Z*Z);
    public Point Unit => Length > 1e-9 ? this / Length : default;
    public double[] Array => [X, Y, Z];
    public Vector3 Float => new((float)X, (float)Y, (float)Z);
    public static Point From(Vector3 v) => new(v.X, v.Y, v.Z);
    public static Point operator +(Point a, Point b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Point operator -(Point a, Point b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Point operator *(Point a, double n) => new(a.X*n,a.Y*n,a.Z*n);
    public static Point operator /(Point a, double n) => a*(1/n);
    public static double Dot(Point a, Point b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    public static Point Cross(Point a, Point b) => new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    public Point Clamp(double max) => Length > max ? Unit*max : this;
}
internal static class NavigationMath
{
    public static Point Parse(double[]? value, string name)
    {
        if (value == null || value.Length != 3 || value.Any(x => !double.IsFinite(x) || Math.Abs(x)>1e12))
            throw new ArgumentException("invalid_"+name);
        return new(value[0],value[1],value[2]);
    }
    public static Quaternion Orientation(Point forward, Point up)
    {
        forward = forward.Unit;
        var right = Point.Cross(forward, up).Unit;
        if (forward.Length < .9 || right.Length < .9) throw new ArgumentException("invalid_orientation");
        up = Point.Cross(right, forward).Unit;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            (float)right.X,(float)right.Y,(float)right.Z,0,
            (float)up.X,(float)up.Y,(float)up.Z,0,
            (float)-forward.X,(float)-forward.Y,(float)-forward.Z,0,0,0,0,1)));
    }
    public static (Point LocalRate, double Angle) Rotation(Quaternion current, Quaternion target)
    {
        var error = Quaternion.Normalize(target * Quaternion.Conjugate(current));
        if (error.W < 0) error = new(-error.X,-error.Y,-error.Z,-error.W);
        double angle = 2*Math.Acos(Math.Clamp(error.W,-1,1));
        var axis = new Point(error.X,error.Y,error.Z).Unit;
        var world = axis*Math.Min(.35, angle*.8);
        return (Point.From(Vector3.Transform(world.Float,Quaternion.Conjugate(current))), angle);
    }
    public static Point Flight(Point error, Point velocity, double maxSpeed, double acceleration, double tolerance)
    {
        double distance = error.Length;
        double speed = Math.Min(maxSpeed, Math.Min(distance*.65, Math.Sqrt(2*acceleration*Math.Max(0,distance-tolerance*.35))));
        var desired = distance < tolerance*.35 ? default : error.Unit*speed;
        return ((desired-velocity)*1.4).Clamp(acceleration);
    }
    public static (double Throttle, double Steering, double Distance, double Heading) Ground(
        Point error, Point forward, Point up, double speed, double maxSpeed, double tolerance)
    {
        up=up.Unit; error-=up*Point.Dot(error,up); forward=(forward-up*Point.Dot(forward,up)).Unit;
        double distance=error.Length;
        double heading=distance<1e-6?0:Math.Atan2(Point.Dot(Point.Cross(forward,error.Unit),up),Point.Dot(forward,error.Unit));
        double desired=Math.Min(maxSpeed,Math.Sqrt(2*1.5*Math.Max(0,distance-tolerance)))*Math.Max(.15,Math.Cos(heading));
        return (Math.Clamp((desired-speed)*.5,-1,1),Math.Clamp(-heading*1.5,-1,1),distance,heading);
    }
}
