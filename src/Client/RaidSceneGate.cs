using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GigaPMC.Client
{
    // A GameWorld and a local Bear/Usec PMC can both exist in the Hideout.
    // Positive location or real map scene evidence is mandatory; unknown
    // environments fail closed. The loaded Hideout scene vetoes ALL captures.
    internal static class RaidSceneGate
    {
        internal static bool IsHideout(string text) => RaidSceneRules.IsHideout(text);
        internal static bool IsKnownMap(string text) => RaidSceneRules.IsKnownMap(text);
        internal static bool IsRealRaid(object world,object player,out string reason)
        {
            reason="NO_WORLD_OR_PLAYER";
            if(world==null || player==null) return false;
            try
            {
                var location=AttributeMemberReader.Read(world,"LocationId") ??
                    AttributeMemberReader.Read(world,"LocationID");
                string locationName=location as string;
                string[] sceneNames=Enumerable.Range(0,SceneManager.sceneCount)
                    .Select(i=>SceneManager.GetSceneAt(i))
                    .Where(s=>s.isLoaded).Select(s=>s.name).ToArray();
                var actor=player as Component;
                string actorScene=actor==null || actor.gameObject==null ? null : actor.gameObject.scene.name;
                // Never mistake a Hideout GameWorld for an actual raid.
                if(IsHideout(locationName) || sceneNames.Any(IsHideout) ||
                    IsHideout(actorScene))
                { reason="HIDEOUT_SCENE_OR_LOCATION"; return false; }
                if(IsKnownMap(locationName) || sceneNames.Any(IsKnownMap) ||
                    IsKnownMap(actorScene))
                { reason="VERIFIED_MAP_SCENE"; return true; }
                reason="NO_VERIFIED_RAID_MAP_SCENE";
                return false;
            }
            catch(Exception ex)
            { reason="SCENE_EVIDENCE_UNAVAILABLE_"+ex.GetType().Name; return false; }
        }
    }
}
