// Expands limited-range video (black = 16, white = 235) to full range for display.
// Windows' built-in video control shows limited-range video without this step, which looks washed out.
sampler2D input : register(s0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 c = tex2D(input, uv);
    // color is premultiplied by alpha in WPF; video is opaque so alpha = 1
    c.rgb = saturate((c.rgb - 0.0627451) * 1.1643836);
    return c;
}
