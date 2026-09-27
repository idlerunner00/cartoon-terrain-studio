using Godot;
using System.Collections.Generic;
namespace Flui;

/// <summary>Surface data for the mandatory comic program, including imported PBR textures.
/// Flags select shared render-state variants, never another lighting implementation.</summary>
[GlobalClass]
public partial class ComicMaterial3D : ShaderMaterial
{
    private Color _albedo=Colors.White,_emission=Colors.Black;
    private float _roughness=.85f,_metallic,_specular=.12f,_energy=1;
    private bool _vertexColor,_vertexSrgb,_emissionEnabled;
    private BaseMaterial3D.TransparencyEnum _transparency;
    private BaseMaterial3D.CullModeEnum _cull;
    private BaseMaterial3D.ShadingModeEnum _shading=BaseMaterial3D.ShadingModeEnum.PerPixel;
    private BaseMaterial3D.BlendModeEnum _blend;
    private bool _noDepth;
    public ComicMaterial3D() => UpdateProgram();
    [Export] public Color AlbedoColor {get=>_albedo;set{_albedo=value;SetShaderParameter("albedo_color",value);}}
    [Export] public float Roughness {get=>_roughness;set{_roughness=value;SetShaderParameter("roughness",value);}}
    [Export] public float Metallic {get=>_metallic;set{_metallic=value;SetShaderParameter("metallic",value);}}
    [Export] public float MetallicSpecular {get=>_specular;set{_specular=value;SetShaderParameter("specular",value);}}
    [Export] public bool VertexColorUseAsAlbedo {get=>_vertexColor;set{_vertexColor=value;SetShaderParameter("vertex_color_enabled",value);}}
    [Export] public bool VertexColorIsSrgb {get=>_vertexSrgb;set{_vertexSrgb=value;SetShaderParameter("vertex_color_srgb",value);}}
    [Export] public bool EmissionEnabled {get=>_emissionEnabled;set{_emissionEnabled=value;SetShaderParameter("emission_enabled",value);}}
    [Export] public Color Emission {get=>_emission;set{_emission=value;SetShaderParameter("emission_color",new Vector3(value.R,value.G,value.B));}}
    [Export] public float EmissionEnergyMultiplier {get=>_energy;set{_energy=value;SetShaderParameter("emission_energy",value);}}
    [Export] public BaseMaterial3D.TransparencyEnum Transparency {get=>_transparency;set{if(_transparency==value)return;_transparency=value;UpdateProgram();}}
    [Export] public BaseMaterial3D.CullModeEnum CullMode {get=>_cull;set{if(_cull==value)return;_cull=value;UpdateProgram();}}
    [Export] public BaseMaterial3D.ShadingModeEnum ShadingMode {get=>_shading;set{if(_shading==value)return;_shading=value;UpdateProgram();}}
    [Export] public BaseMaterial3D.BlendModeEnum BlendMode {get=>_blend;set{if(_blend==value)return;_blend=value;UpdateProgram();}}
    [Export] public bool NoDepthTest {get=>_noDepth;set{if(_noDepth==value)return;_noDepth=value;UpdateProgram();}}
    private void UpdateProgram()
    {
        var flags=new List<string>();
        if(_shading==BaseMaterial3D.ShadingModeEnum.Unshaded)flags.Add("COMIC_UNLIT");
        if(_cull==BaseMaterial3D.CullModeEnum.Disabled)flags.Add("COMIC_DOUBLE_SIDED");
        if(_cull==BaseMaterial3D.CullModeEnum.Front)flags.Add("COMIC_CULL_FRONT");
        if(_transparency==BaseMaterial3D.TransparencyEnum.AlphaScissor)flags.Add("COMIC_ALPHA_SCISSOR");
        else if(_transparency==BaseMaterial3D.TransparencyEnum.AlphaHash)flags.Add("COMIC_ALPHA_HASH");
        else if(_transparency!=BaseMaterial3D.TransparencyEnum.Disabled)flags.Add("COMIC_ALPHA");
        if(_transparency==BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass)flags.Add("COMIC_DEPTH_PREPASS");
        if(_noDepth)flags.Add("COMIC_NO_DEPTH_TEST");
        if(_blend==BaseMaterial3D.BlendModeEnum.Add)flags.Add("COMIC_ADDITIVE");
        Shader=ComicShaders.Variant("res://assets/shaders/comic_material.gdshader",flags.ToArray());
        SetShaderParameter("comic_role",_shading==BaseMaterial3D.ShadingModeEnum.Unshaded?4:0);
    }
    public void CopySurface(BaseMaterial3D source)
    {
        ResourceName=source.ResourceName;
        AlbedoColor=source.AlbedoColor;Roughness=source.Roughness;Metallic=source.Metallic;MetallicSpecular=source.MetallicSpecular;
        VertexColorUseAsAlbedo=source.VertexColorUseAsAlbedo;VertexColorIsSrgb=source.VertexColorIsSrgb;
        EmissionEnabled=source.EmissionEnabled;Emission=source.Emission;EmissionEnergyMultiplier=source.EmissionEnergyMultiplier;
        Transparency=source.Transparency;CullMode=source.CullMode;ShadingMode=source.ShadingMode;BlendMode=source.BlendMode;NoDepthTest=source.NoDepthTest;
        SetShaderParameter("albedo_texture",source.AlbedoTexture);
        SetShaderParameter("normal_enabled",source.NormalEnabled);SetShaderParameter("normal_texture",source.NormalTexture);SetShaderParameter("normal_scale",source.NormalScale);
        SetShaderParameter("roughness_texture",source.RoughnessTexture);SetShaderParameter("roughness_channel",Channel(source.RoughnessTextureChannel));
        SetShaderParameter("metallic_texture",source.MetallicTexture);SetShaderParameter("metallic_channel",Channel(source.MetallicTextureChannel));
        SetShaderParameter("ao_enabled",source.AOEnabled);SetShaderParameter("ao_texture",source.AOTexture);SetShaderParameter("ao_channel",Channel(source.AOTextureChannel));
        SetShaderParameter("ao_light_affect",source.AOLightAffect);SetShaderParameter("emission_texture",source.EmissionTexture);
        SetShaderParameter("emission_add_texture",source.EmissionTexture!=null&&source.EmissionOperator==BaseMaterial3D.EmissionOperatorEnum.Add);
        SetShaderParameter("alpha_scissor",source.AlphaScissorThreshold);
        SetShaderParameter("uv_scale",source.Uv1Scale);SetShaderParameter("uv_offset",source.Uv1Offset);
        RenderPriority=source.RenderPriority;
        // glTF may pack all three channels into an ORM texture.
        if(source is OrmMaterial3D orm) {
            SetShaderParameter("roughness_texture",orm.OrmTexture);SetShaderParameter("roughness_channel",new Vector4(0,1,0,0));
            SetShaderParameter("metallic_texture",orm.OrmTexture);SetShaderParameter("metallic_channel",new Vector4(0,0,1,0));
            SetShaderParameter("ao_texture",orm.OrmTexture);SetShaderParameter("ao_channel",new Vector4(1,0,0,0));
        }
    }
    private static Vector4 Channel(BaseMaterial3D.TextureChannel channel) => (int)channel switch {
        1=>new(0,1,0,0),2=>new(0,0,1,0),3=>new(0,0,0,1),4=>new(1f/3,1f/3,1f/3,0),_=>new(1,0,0,0)};
}
