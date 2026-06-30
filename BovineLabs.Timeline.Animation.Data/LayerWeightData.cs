using Unity.Entities;

namespace BovineLabs.Timeline.Animation
{
    /// <summary>
    /// Track-level config for a LayerWeight track: which animation layer this track's clips fade. Mirrors the
    /// LayerIndex on RukhankaAnimationTrack / BlendTree2DTrack so a LayerWeight track can target the same layer.
    /// </summary>
    public struct LayerWeightTrackData : IComponentData
    {
        public int LayerIndex;
    }

    /// <summary>
    /// Clip-level config for a LayerWeight clip: an upper bound on the multiplier. The per-frame multiplier is
    /// the clip's timeline ease (ClipWeight) times this max, so the clip's blend in/out handles ARE the layer
    /// weight curve and MaxMultiplier just caps how high the layer can rise while this clip is active.
    /// </summary>
    public struct LayerWeightClipData : IComponentData
    {
        public float MaxMultiplier;
    }

    /// <summary>
    /// Per-actor, per-frame authored override of an animation layer's overall weight, keyed by LayerIndex.
    /// Written by <see cref="TimelineLayerWeightTrackSystem"/> from active LayerWeight clips and consumed by the
    /// layer-mixing pass in TimelineAnimationUnificationSystem. The buffer is rebuilt every frame ("set this
    /// frame" semantics); a layer with no entry means multiplier 1 (no override), so behavior is unchanged when
    /// no LayerWeight track is present.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct LayerWeightOverride : IBufferElementData
    {
        public int LayerIndex;
        public float Multiplier;
    }
}
