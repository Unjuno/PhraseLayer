Shader "PhraseLayer/SourceMask"
{
    Properties
    {
        _Color ("Mask Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "Queue"="Geometry+10" "RenderType"="Opaque" }
        Cull Off
        ZWrite On
        ZTest LEqual
        Lighting Off

        Pass
        {
            Color [_Color]
        }
    }

    Fallback Off
}
