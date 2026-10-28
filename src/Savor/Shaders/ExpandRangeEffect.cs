using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Savor.Shaders;

/// <summary>
/// GPU pixel shader that stretches limited-range video (16–235) to full range (0–255).
/// Windows' built-in video control skips this step, which makes normal videos look washed out.
/// </summary>
public sealed class ExpandRangeEffect : ShaderEffect
{
    static readonly Uri ShaderUri = new("pack://application:,,,/Shaders/ExpandRange.ps");
    static bool? _available;

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(ExpandRangeEffect), 0);

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    ExpandRangeEffect(PixelShader ps)
    {
        PixelShader = ps;
        UpdateShaderValue(InputProperty);
    }

    /// <summary>Returns the effect, or null if the compiled shader isn't in this build.</summary>
    public static ExpandRangeEffect? TryCreate()
    {
        if (_available == false) return null;
        try
        {
            var ps = new PixelShader { UriSource = ShaderUri };
            _available = true;
            return new ExpandRangeEffect(ps);
        }
        catch
        {
            _available = false;
            return null;
        }
    }
}
