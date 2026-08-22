using Mono.Cecil;
using Mono.Cecil.Cil;
using SteamP2PFriends.Patches;
using System;
using System.Linq;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class RemoteCollisionAnimationPolicyTests
    {
        internal static bool Test_RC1_CullingPolicyIsSavedAndRestored()
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(
                typeof(LevelObjectRemoteCollisionPatch).Assembly.Location))
            {
                TypeDefinition type = FindPatchType(module);
                if (type == null)
                    return false;

                FieldDefinition tracking = type.Fields.FirstOrDefault(field =>
                    field.Name == "RemoteAnimationCulling" &&
                    field.FieldType.FullName.Contains("UnityEngine.Animation") &&
                    field.FieldType.FullName.Contains("UnityEngine.AnimationCullingType"));
                MethodDefinition apply = FindMethod(type, "ApplyRemoteAnimationPolicy");
                MethodDefinition restoreObject = FindMethod(type, "RestoreRemoteAnimationPolicy");
                MethodDefinition restoreAll = FindMethod(type, "RestoreAllRemoteAnimationPolicies");

                return tracking != null &&
                    Calls(apply, "System.Collections.Generic.Dictionary`2", "Add") &&
                    Calls(apply, "UnityEngine.Animation", "set_cullingType") &&
                    Calls(restoreObject, "UnityEngine.Animation", "set_cullingType") &&
                    Calls(restoreObject, "System.Collections.Generic.Dictionary`2", "Remove") &&
                    Calls(restoreAll, "UnityEngine.Animation", "set_cullingType") &&
                    Calls(restoreAll, "System.Collections.Generic.Dictionary`2", "Clear");
            }
        }

        internal static bool Test_RC2_CullingPolicyPrecedesRootActivation()
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(
                typeof(LevelObjectRemoteCollisionPatch).Assembly.Location))
            {
                TypeDefinition type = FindPatchType(module);
                MethodDefinition postfix = FindMethod(type, "UpdateActiveAndRenderersEnabled_Postfix");
                if (postfix?.Body == null)
                    return false;

                int policyIndex = FindCallIndex(postfix, type.FullName, "ApplyRemoteAnimationPolicy");
                int activationIndex = FindCallIndex(postfix, "UnityEngine.GameObject", "SetActive");
                return policyIndex >= 0 && activationIndex >= 0 && policyIndex < activationIndex;
            }
        }

        private static TypeDefinition FindPatchType(ModuleDefinition module)
        {
            return module.Types.FirstOrDefault(type =>
                type.FullName == typeof(LevelObjectRemoteCollisionPatch).FullName);
        }

        private static MethodDefinition FindMethod(TypeDefinition type, string name)
        {
            return type?.Methods.FirstOrDefault(method => method.Name == name);
        }

        private static bool Calls(MethodDefinition method, string declaringType, string methodName)
        {
            return FindCallIndex(method, declaringType, methodName) >= 0;
        }

        private static int FindCallIndex(MethodDefinition method, string declaringType, string methodName)
        {
            if (method?.Body == null)
                return -1;

            for (int index = 0; index < method.Body.Instructions.Count; index++)
            {
                Instruction instruction = method.Body.Instructions[index];
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                    continue;
                if (!(instruction.Operand is MethodReference target))
                    continue;
                if (target.Name == methodName && target.DeclaringType.FullName.StartsWith(declaringType, StringComparison.Ordinal))
                    return index;
            }
            return -1;
        }
    }
}
