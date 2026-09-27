// Shared world pigments. Flui's character shaders deliberately do not include this file.
float pigment_hash(vec3 p) {
    p=fract(p*.1031);p+=dot(p,p.yzx+33.33);return fract((p.x+p.y)*p.z);
}
vec3 blossom_pigment(vec3 color,vec3 p,vec3 n) {
    vec3 axis=abs(n);
    vec2 plane=axis.y>max(axis.x,axis.z)?p.xz:axis.x>axis.z?p.zy:p.xy;
    vec2 grid=plane*7.8+vec2(sin(plane.y*3.71),cos(plane.x*3.43))*1.18;
    vec2 cell=floor(grid),f=fract(grid)-.5;
    float seed=pigment_hash(vec3(cell,floor(dot(p,n)*2.0)));
    f-=vec2(seed-.5,pigment_hash(vec3(cell+19.0,7.0))-.5)*.30;
    float angle=atan(f.y,f.x)+seed*6.283;
    float edge=length(f)-(.36+.055*cos(angle*5.0));
    float aa=max(.015,length(fwidth(grid))*.55);
    float petal=1.0-smoothstep(-aa,aa,edge);
    float heart=1.0-smoothstep(.035,.08+aa,length(f));
    float detail=1.0-smoothstep(.45,1.6,length(fwidth(grid)));
    vec3 flower=mix(color*.88,color*(.97+seed*.09),petal);
    flower=mix(flower,vec3(.91,.61,.47),heart*.22);
    float cluster=sin(p.x*1.73+sin(p.z*2.13))*cos(p.y*2.37+p.z*.91);
    return mix(color*(.99+cluster*.035),flower,detail*.42);
}
vec3 leaf_pigment(vec3 color,vec3 p,vec3 n) {
    vec3 axis=abs(n);
    vec2 plane=axis.y>max(axis.x,axis.z)?p.xz:axis.x>axis.z?p.zy:p.xy;
    vec2 grid=plane*10.0+vec2(sin(plane.y*4.1),sin(plane.x*3.7))*.6;
    vec2 cell=floor(grid),f=fract(grid)-.5;
    float seed=pigment_hash(vec3(cell,floor(dot(p,n)*3.0)));
    float angle=seed*6.283;
    f=mat2(vec2(cos(angle),sin(angle)),vec2(-sin(angle),cos(angle)))*f;
    float aa=max(.012,length(fwidth(grid))*.45);
    float edge=length(f*vec2(1.2,2.0))-.46;
    float leaf=1.0-smoothstep(-aa,aa,edge);
    float vein=(1.0-smoothstep(.006,.025+aa,abs(f.y)))*leaf;
    float detail=1.0-smoothstep(.35,1.3,length(fwidth(grid)));
    float cluster=sin(p.x*2.73+sin(p.z*3.13))*cos(p.y*2.37+p.z*.91);
    return color*(1.0+cluster*.045+(-.16+leaf*(.14+seed*.14)+vein*.07)*detail);
}
vec3 bark_pigment(vec3 color,vec3 p) {
    float grain=sin(p.y*.18+sin(p.x*.43+p.z*.39)*2.0+p.x*6.0+p.z*5.0);
    float detail=1.0-smoothstep(.25,1.5,length(fwidth(p.xz*4.0)));
    float fissure=smoothstep(.80,.97,grain);
    float plates=pigment_hash(floor(vec3(p.x*2.0,p.y*.55,p.z*2.0)));
    return color*(1.0-fissure*.24*detail+plates*.10*detail);
}
vec3 world_pigment(vec3 color, float matter) {
    float green = smoothstep(1.6, 2.8, color.g / max(color.r, 0.015));
    float luminance = dot(color, vec3(0.2126, 0.7152, 0.0722));
    // Preserve pale/pink flowers and existing neutral architecture; connect vegetation through teal.
    if ((matter > 0.5 && matter < 1.5) || (matter > 6.5 && matter < 7.5)) {
        vec3 moss = mix(vec3(.125,.195,.181),vec3(.334,.402,.314),clamp(sqrt(luminance),0.0,1.0));
        color = mix(color, moss, green * .90);
    }
    return color;
}
