using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
namespace Flui;

/// <summary>One cache and admission path for all prototype shader programs.</summary>
public static class ComicShaders
{
    public const string Core = "res://assets/shaders/comic_style.gdshaderinc";
    public const string Palette = "res://assets/shaders/comic_palette.gdshaderinc";
    private static readonly Dictionary<string,Shader> Programs = new();
    // Shader.set_code and include dependency recompilation both emit Changed.
    // The table does not own shaders. Entries and signal delegates do not retain
    // their key, so temporary imported programs can be collected normally.
    private sealed class Validation
    {
        private long _revision, _validated = -1;
        private string? _error;
        // A variant is built in code and has no resource path; it is admitted as its source shader.
        public string? Identity;
        public void Invalidate() => Interlocked.Increment(ref _revision);
        public void Require(Shader shader)
        {
            lock (this) {
                long revision = Volatile.Read(ref _revision);
                if (_validated != revision) {
                    _error = null;
                    try { RequireCentralStyle(shader.Code, Identity ?? shader.ResourcePath); }
                    catch (InvalidOperationException error) { _error = error.Message; }
                    _validated = revision;
                    Interlocked.Increment(ref ValidationReads);
                }
                if (_error != null) throw new InvalidOperationException(_error);
            }
        }
    }
    private static readonly ConditionalWeakTable<Shader,Validation> Validations = new();
    internal static long ValidationReads;
    private sealed class VariantSource
    {
        private readonly Shader _source, _variant;
        private readonly string _flags;
        public VariantSource(Shader source,Shader variant,string flags){_source=source;_variant=variant;_flags=flags;}
        public void Refresh()=>_variant.Code=_flags+"\n"+_source.Code;
    }
    internal static void RequireCentralStyle(Shader shader, string? identity = null)
    {
        var validation = Validations.GetValue(shader, static program => {
            var validation = new Validation();
            program.Changed += validation.Invalidate;
            return validation;
        });
        if (identity != null) validation.Identity = identity;
        validation.Require(shader);
    }
    public static Shader Load(string path)
    {
        if (Programs.TryGetValue(path,out var cached)) { RequireCentralStyle(cached); return cached; }
        var shader=GD.Load<Shader>(path)??throw new InvalidOperationException("Missing shader: "+path);
        RequireCentralStyle(shader);
        Programs.Add(path,shader);return shader;
    }
    public static Shader Variant(string path,params string[] defines)
    {
        if(defines.Length==0)return Load(path);
        foreach(var define in defines)
            if(!System.Text.RegularExpressions.Regex.IsMatch(define,@"^[A-Z][A-Z0-9_]*$"))
                throw new ArgumentException("Shader variants accept named feature flags only",nameof(defines));
        string flags=string.Join("\n",defines.Distinct().OrderBy(s=>s).Select(s=>"#define "+s));
        string key=path+"\n"+flags;
        if(Programs.TryGetValue(key,out var cached)){RequireCentralStyle(cached);return cached;}
        var source=Load(path);
        var shader=new Shader{Code=flags+"\n"+source.Code};
        var owner=new VariantSource(source,shader,flags);
        source.Changed+=owner.Refresh;
        RequireCentralStyle(shader,path);
        Programs.Add(key,shader);return shader;
    }
    public static void RequireCentralStyle(string code,string identity)
    {
        bool spatial=code.Contains("shader_type spatial;");
        bool outline=identity=="res://assets/shaders/world_outline.gdshader";
        if(spatial&&!(outline?code.Contains(Palette):code.Contains(Core)))
            throw new InvalidOperationException("3D shader bypasses the mandatory comic pipeline: "+identity);
        if(spatial&&!outline&&(!code.Contains("COMIC_VERTEX")||!(code.Contains("COMIC_FRAGMENT")||code.Contains("comic_surface("))))
            throw new InvalidOperationException("Surface does not apply central comic ink: "+identity);
        if(System.Text.RegularExpressions.Regex.IsMatch(code,@"\bvoid\s+light\s*\("))
            throw new InvalidOperationException("A material cannot own a second light() implementation: "+identity);
    }
}
