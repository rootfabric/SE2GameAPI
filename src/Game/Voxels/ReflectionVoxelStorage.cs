using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
namespace SE2GameAPI.Game.Voxels;

// Version-tolerant binding to the actual loaded DLLs, not SE1 types. A missing member
// is UNSUPPORTED, never an empty tile. Only explicit storage-coordinate transforms
// are accepted; guessing a planet's centre/voxel size would produce plausible bad maps.
internal sealed class ReflectionVoxelStorage : IVoxelStorage
{
    internal const BindingFlags Members=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private readonly object reader;
    private readonly object? provider;
    private readonly MethodInfo read,resize;
    private readonly Type dataType,vectorType;
    private readonly object contentFlag,requestFlags;
    private readonly ConstructorInfo? constructor;
    private static readonly ConcurrentDictionary<Type,Func<object,int,int,int,byte>> accessors=new();
    private readonly Func<object,int,int,int,byte> content;
    private readonly Func<bool> alive;
    public string Id { get; }
    public object Identity=>reader;
    public I3 Size { get; }
    public VoxelFrame Frame { get; }
    public string? Revision=>Get(reader,"Revision","Version")?.ToString();

    private ReflectionVoxelStorage(string id,object target,object? dataProvider,MethodInfo method,
        I3 size,VoxelFrame frame,Func<bool> isAlive)
    {
        Id=id; reader=target; provider=dataProvider; read=method; Size=size; Frame=frame; alive=isAlive;
        var ps=read.GetParameters(); int offset=ps.Length==7?1:0;
        dataType=Element(ps[offset].ParameterType); vectorType=Element(ps[offset+3].ParameterType);
        if(dataType.IsByRefLike) throw new NotSupportedException("ref_struct_storage_data_requires_typed_adapter");
        contentFlag=Enum.Parse(Element(ps[offset+1].ParameterType),"Content",false);
        requestFlags=Activator.CreateInstance(Element(ps[^1].ParameterType))!;
        constructor=dataType.GetConstructor(Members,null,[contentFlag.GetType()],null);
        if(constructor==null && dataType.GetConstructor(Type.EmptyTypes)==null)
            throw new NotSupportedException("storage_data_constructor_unavailable");
        resize=dataType.GetMethods(Members).FirstOrDefault(m=>m.Name=="Resize" &&
            m.GetParameters().Length is 1 or 2 && m.GetParameters().All(p=>Element(p.ParameterType)==vectorType))
            ?? throw new NotSupportedException("storage_data_resize_unavailable");
        var get=dataType.GetMethods(Members).FirstOrDefault(m=>m.Name=="Content" && m.ReturnType==typeof(byte) &&
            m.GetParameters().Length==1 && Element(m.GetParameters()[0].ParameterType)==vectorType)
            ?? throw new NotSupportedException("uint8_content_accessor_unavailable");
        content=accessors.GetOrAdd(dataType,_=>BuildAccessor(dataType,vectorType,get));
    }
    private static Func<object,int,int,int,byte> BuildAccessor(Type dataType,Type vectorType,MethodInfo get)
    {
        // Once per DLL type, not once per tile/storage/scan and not reflection per sample.
        var obj=Expression.Parameter(typeof(object)); var x=Expression.Parameter(typeof(int));
        var y=Expression.Parameter(typeof(int)); var z=Expression.Parameter(typeof(int));
        var v=Expression.Variable(vectorType); var d=Expression.Variable(dataType);
        var ctor=vectorType.GetConstructor([typeof(int),typeof(int),typeof(int)])
            ?? throw new NotSupportedException("integer_vector_constructor_unavailable");
        return Expression.Lambda<Func<object,int,int,int,byte>>(Expression.Block([v,d],
            Expression.Assign(v,Expression.New(ctor,x,y,z)),Expression.Assign(d,Expression.Convert(obj,dataType)),
            Expression.Call(d,get,v)),obj,x,y,z).Compile();
    }

    public byte[] Read(I3 min,I3 size)
    {
        if(!alive()) throw new InvalidOperationException("voxel_entity_unloaded");
        if(size.X<=0 || size.Y<=0 || size.Z<=0 || size.X>16 || size.Y>16 || size.Z>16 ||
            min.X<0 || min.Y<0 || min.Z<0 || (long)min.X+size.X>Size.X || (long)min.Y+size.Y>Size.Y || (long)min.Z+size.Z>Size.Z)
            throw new ArgumentOutOfRangeException(nameof(size));
        object data=constructor!=null?constructor.Invoke([contentFlag]):Activator.CreateInstance(dataType)!;
        try
        {
            object Vec(int x,int y,int z)=>Activator.CreateInstance(vectorType,x,y,z)!;
            object a=Vec(min.X,min.Y,min.Z), b=Vec(min.X+size.X-1,min.Y+size.Y-1,min.Z+size.Z-1);
            resize.Invoke(data,resize.GetParameters().Length==1?[Vec(size.X,size.Y,size.Z)]:[a,b]);
            object?[] args=provider==null?[data,contentFlag,0,a,b,requestFlags]:[provider,data,contentFlag,0,a,b,requestFlags];
            read.Invoke(reader,args);
            data=args[provider==null?0:1]!; // ref StorageData may replace its buffer/boxed struct.
            var result=new byte[size.Count]; int i=0;
            for(int z=0;z<size.Z;z++) for(int y=0;y<size.Y;y++) for(int x=0;x<size.X;x++)
                result[i++]=content(data,x,y,z);
            return result;
        }
        finally { if(data is IDisposable disposable) disposable.Dispose(); }
    }
    internal static Type Element(Type t)=>t.IsByRef?t.GetElementType()!:t;
    internal static object? Get(object? target,params string[] names)
    {
        if(target==null) return null;
        foreach(string name in names)
        {
            var type=target.GetType();
            var property=type.GetProperty(name,Members|BindingFlags.IgnoreCase);
            if(property!=null && property.GetIndexParameters().Length==0 && !property.PropertyType.IsByRefLike)
            { var value=property.GetValue(target); if(value!=null) return value; }
            for(Type? t=type;t!=null;t=t.BaseType)
            {
                var f=t.GetFields(Members|BindingFlags.DeclaredOnly).FirstOrDefault(f=>
                    string.Equals(f.Name,name,StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.Name,"_"+name,StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.Name,"m_"+name,StringComparison.OrdinalIgnoreCase) || f.Name=="<"+name+">k__BackingField");
                if(f!=null && !f.FieldType.IsByRefLike) { var value=f.GetValue(target); if(value!=null) return value; }
            }
        }
        return null;
    }
    internal static V3 Vector(object value)=>new(Convert.ToDouble(Get(value,"X")),Convert.ToDouble(Get(value,"Y")),Convert.ToDouble(Get(value,"Z")));
    private static bool HasVector(object v)=>Get(v,"X")!=null && Get(v,"Y")!=null && Get(v,"Z")!=null;
    private static bool Signature(MethodInfo m)
    {
        var p=m.GetParameters(); int o=p.Length==7?1:0;
        return m.Name=="ReadRange" && !m.IsStatic && m.ReturnType==typeof(void) && p.Length is 6 or 7 &&
            (o==0 || Element(p[0].ParameterType).Name=="IStorageDataProvider") &&
            Element(p[o].ParameterType).Name=="StorageData" && p[o].ParameterType.IsByRef &&
            Element(p[o+1].ParameterType).Name=="StorageDataTypeFlags" && Element(p[o+1].ParameterType).IsEnum &&
            p[o+2].ParameterType==typeof(int) && Element(p[o+3].ParameterType).Name=="Vector3I" &&
            Element(p[o+4].ParameterType)==Element(p[o+3].ParameterType) &&
            Element(p[^1].ParameterType).Name=="VoxelRequestFlags" && Element(p[^1].ParameterType).IsEnum;
    }
    internal static ReflectionVoxelStorage Bind(string id,object component,Func<bool> alive)
    {
        var nodes=new List<object>{component};
        string[] links=["Storage","StorageHandler","StorageHandle","Voxel","VoxelComponent","CoordinateSystem","StorageCoordinates","StorageTransform","DataProvider","StorageProvider","Provider"];
        for(int i=0;i<nodes.Count && i<24;i++) foreach(string link in links)
        {
            object? node;
            try { node=Get(nodes[i],link); } catch { continue; }
            if(node!=null && nodes.Count<24 && !nodes.Any(n=>ReferenceEquals(n,node))) nodes.Add(node);
        }
        var choices=nodes.SelectMany(n=>n.GetType().GetMethods(Members).Where(Signature).Select(m=>(Target:n,Method:m)))
            .OrderBy(p=>p.Method.GetParameters().Length).ToArray();
        if(choices.Length==0) throw new NotSupportedException($"storage_reader_unavailable:{component.GetType().FullName}");
        Exception? failure=null;
        foreach(var choice in choices)
        {
            try
            {
                var ps=choice.Method.GetParameters();
                object? provider=ps.Length==7?nodes.FirstOrDefault(n=>ps[0].ParameterType.IsInstanceOfType(n)):null;
                if(ps.Length==7 && provider==null) throw new NotSupportedException("data_provider_unavailable");
                var rawSize=Get(choice.Target,"Size") ?? nodes.Select(n=>Get(n,"Size")).FirstOrDefault(v=>v!=null && HasVector(v));
                if(rawSize==null || !HasVector(rawSize)) throw new NotSupportedException("storage_size_unavailable");
                var v=Vector(rawSize);
                if(!v.Finite || v.X<1 || v.Y<1 || v.Z<1 || v.X>int.MaxValue || v.Y>int.MaxValue || v.Z>int.MaxValue ||
                    v.X!=Math.Floor(v.X) || v.Y!=Math.Floor(v.Y) || v.Z!=Math.Floor(v.Z))
                    throw new NotSupportedException("invalid_storage_size");
                var frame=nodes.Select(TryFrame).FirstOrDefault(f=>f!=null && f.Valid)
                    ?? throw new NotSupportedException($"storage_to_world_unavailable:{component.GetType().FullName}");
                return new(id,choice.Target,provider,choice.Method,new((int)v.X,(int)v.Y,(int)v.Z),frame,alive);
            }
            catch(Exception e) { failure=e; }
        }
        throw new NotSupportedException(failure?.GetBaseException().Message ?? "unsupported_voxel_api");
    }
    private static VoxelFrame? TryFrame(object obj)
    {
        try
        {
            // Explicit storage-to-world affine matrices. Entity.WorldMatrix is deliberately NOT accepted.
            var matrix=Get(obj,"StorageToWorldMatrix","VoxelToWorldMatrix","StorageToWorld");
            if(matrix!=null && Get(matrix,"M41")!=null)
            {
                double M(string name)=>Convert.ToDouble(Get(matrix,name) ?? throw new NotSupportedException(name));
                return new(new(M("M41"),M("M42"),M("M43")),new(M("M11"),M("M12"),M("M13")),
                    new(M("M21"),M("M22"),M("M23")),new(M("M31"),M("M32"),M("M33")));
            }
            foreach(var method in obj.GetType().GetMethods(Members).Where(m=>
                m.Name is "VoxelCoordToWorldPosition" or "StorageToWorld" or "VoxelToWorld"))
            {
                var p=method.GetParameters();
                if(method.IsStatic || p.Length!=1 || method.ReturnType==typeof(void)) continue;
                var t=Element(p[0].ParameterType);
                var ctor=t.GetConstructors().FirstOrDefault(c=>c.GetParameters().Length==3 &&
                    c.GetParameters().All(a=>a.ParameterType==typeof(int) || a.ParameterType==typeof(float) || a.ParameterType==typeof(double)));
                if(ctor==null) continue;
                V3 ConvertPoint(int x,int y,int z)
                {
                    int[] values=[x,y,z]; var types=ctor.GetParameters();
                    var input=ctor.Invoke(values.Select((v,i)=>Convert.ChangeType(v,types[i].ParameterType)).ToArray());
                    var result=method.Invoke(obj,[input])!;
                    if(!HasVector(result)) throw new NotSupportedException("invalid_coordinate_result");
                    return Vector(result);
                }
                var origin=ConvertPoint(0,0,0);
                var frame=new VoxelFrame(origin,ConvertPoint(1,0,0)-origin,ConvertPoint(0,1,0)-origin,ConvertPoint(0,0,1)-origin);
                var check=ConvertPoint(7,9,11)-frame.World(new(7,9,11));
                if(V3.Dot(check,check)>1e-6) throw new NotSupportedException("non_affine_voxel_coordinates");
                return frame;
            }

        }
        catch { /* Another node may expose a supported coordinate contract. */ }
        return null;
    }
}
