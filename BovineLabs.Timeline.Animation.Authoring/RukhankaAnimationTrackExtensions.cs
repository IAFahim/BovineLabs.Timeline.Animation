using Rukhanka;
using Rukhanka.Hybrid;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Hash128 = Unity.Entities.Hash128;

namespace BovineLabs.Timeline.Animation.Authoring
{
    public static class RukhankaAnimationTrackExtensions
    {
        public static RigDefinitionAuthoring ResolveRigDefinition(this PlayableDirector director, TrackAsset track)
        {
            var binding = director.GetGenericBinding(track);

            if (binding is RigDefinitionAuthoring rda)
                return rda;

            if (binding is Animator animator)
                return animator.GetComponent<RigDefinitionAuthoring>();

            if (binding is GameObject go)
                return go.GetComponent<RigDefinitionAuthoring>();

            return null;
        }

        public static bool TryComputeHash(this AnimationClip clip, Avatar avatar, out Hash128 hash)
        {
            if (clip != null)
            {
                hash = BakingUtils.ComputeAnimationHash(clip, avatar);
                return true;
            }

            hash = default;
            return false;
        }

        public static void AddValidAnimations(
            this DynamicBuffer<NewBlobAssetDatabaseRecord<AnimationClipBlob>> buffer,
            NativeArray<BlobAssetReference<AnimationClipBlob>> bakedAnimations)
        {
            // #35: plain foreach over the NativeArray (no LINQ enumerator boxing).
            foreach (var ba in bakedAnimations)
            {
                if (ba == BlobAssetReference<AnimationClipBlob>.Null)
                    continue;

                buffer.Add(new NewBlobAssetDatabaseRecord<AnimationClipBlob>
                {
                    hash = ba.Value.hash,
                    value = ba
                });
            }
        }
    }
}