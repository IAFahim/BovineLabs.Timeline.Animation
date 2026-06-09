#if UNITY_EDITOR

using System;
using Unity.Entities;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;

namespace BovineLabs.Timeline.Animation.Editor
{
    [InitializeOnLoad]
    internal static class AnimationPreviewUpdater
    {
        private static PlayableDirector s_Director;
        private static double s_LastTime = -1d;

        static AnimationPreviewUpdater()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (Application.isPlaying || EditorApplication.isCompiling)
            {
                return;
            }

            var director = TimelineEditor.inspectedDirector;

            if (director == null)
            {
                s_Director = null;
                s_LastTime = -1d;
                return;
            }

            if (director != s_Director)
            {
                s_Director = director;
                s_LastTime = -1d;
            }

            if (!director.playableGraph.IsValid())
            {
                return;
            }

            var time = director.time;

            if (Math.Abs(time - s_LastTime) < 0.0001d)
            {
                return;
            }

            s_LastTime = time;
            director.playableGraph.Evaluate();

            foreach (var world in World.All)
            {
                if ((world.Flags & WorldFlags.Editor) != WorldFlags.Editor)
                {
                    continue;
                }

                world.Update();
                break;
            }
        }
    }
}

#endif
