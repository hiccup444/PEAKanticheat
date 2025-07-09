using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AntiCheatPreloader
{
    public static class AntiCheatPatcher
    {
        // Target the main game assembly
        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

        // List of RPC methods that need PhotonMessageInfo parameter added
        public static List<string> TargetMethods { get; } = new List<string>
        {
            "System.Void Campfire::SetFireWoodCount(System.Int32)",
            "System.Void Campfire::Extinguish_Rpc()",
            "System.Void Character::RPCA_Die(UnityEngine.Vector3)",
            "System.Void Character::RPCA_ReviveAtPosition(UnityEngine.Vector3,System.Boolean)",
            "System.Void Bugfix::AttachBug(System.Int32)"
        };

        // Main patching method
        public static void Patch(AssemblyDefinition assembly)
        {
            // Find PhotonMessageInfo type
            TypeReference photonMessageInfoType;
            if (!assembly.MainModule.TryGetTypeReference("Photon.Pun.PhotonMessageInfo", out photonMessageInfoType))
            {
                Console.Error.WriteLine("[AntiCheatPatcher] PhotonMessageInfo type not found!");
                return;
            }

            List<MethodDefinition> patchedMethods = new List<MethodDefinition>();
            
            Console.WriteLine("[AntiCheatPatcher] Adding PhotonMessageInfo parameter to RPC methods...");

            // First pass: Add PhotonMessageInfo parameter to target methods
            foreach (TypeDefinition type in assembly.MainModule.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    try
                    {
                        if (TargetMethods.Contains(method.FullName) && method.HasBody)
                        {
                            // Check if it's actually an RPC method
                            bool isRPC = method.CustomAttributes.Any(attr => 
                                attr.AttributeType.ToString() == "Photon.Pun.PunRPC");

                            if (isRPC)
                            {
                                // Check if it already has PhotonMessageInfo parameter
                                bool hasMessageInfo = method.Parameters.Any(param => 
                                    param.ParameterType.ToString() == "Photon.Pun.PhotonMessageInfo");

                                if (!hasMessageInfo)
                                {
                                    Console.WriteLine($"[AntiCheatPatcher] Patching {method.FullName}...");
                                    
                                    // Add PhotonMessageInfo parameter
                                    var infoParam = new ParameterDefinition("info", ParameterAttributes.None, photonMessageInfoType);
                                    method.Parameters.Add(infoParam);
                                    
                                    patchedMethods.Add(method);
                                    Console.WriteLine($"[AntiCheatPatcher] Successfully patched {method.FullName}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AntiCheatPatcher] Error patching {method.FullName}: {ex}");
                    }
                }
            }

            Console.WriteLine($"[AntiCheatPatcher] Patched {patchedMethods.Count} methods");
            Console.WriteLine("[AntiCheatPatcher] Adding default PhotonMessageInfo to method calls...");

            // Second pass: Add default PhotonMessageInfo argument to all calls
            foreach (TypeDefinition type in assembly.MainModule.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody) continue;

                    try
                    {
                        ILProcessor il = method.Body.GetILProcessor();
                        List<Instruction> instructionsToProcess = new List<Instruction>();

                        // Find all calls to patched methods
                        foreach (Instruction instruction in method.Body.Instructions)
                        {
                            foreach (MethodDefinition patchedMethod in patchedMethods)
                            {
                                if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                                {
                                    var calledMethod = instruction.Operand as MethodReference;
                                    if (calledMethod != null && calledMethod.FullName == patchedMethod.FullName)
                                    {
                                        instructionsToProcess.Add(instruction);
                                    }
                                }
                            }
                        }

                        if (instructionsToProcess.Count > 0)
                        {
                            // Add PhotonMessageInfo variable to method
                            method.Body.Variables.Add(new VariableDefinition(photonMessageInfoType));
                            var infoVarIndex = method.Body.Variables.Count - 1;

                            foreach (Instruction callInstruction in instructionsToProcess)
                            {
                                Console.WriteLine($"[AntiCheatPatcher] Patching call in {method.FullName}...");

                                // Initialize PhotonMessageInfo with default value
                                il.InsertBefore(callInstruction, il.Create(OpCodes.Ldloca_S, (byte)infoVarIndex));
                                il.InsertBefore(callInstruction, il.Create(OpCodes.Initobj, photonMessageInfoType));
                                il.InsertBefore(callInstruction, il.Create(OpCodes.Ldloc_S, (byte)infoVarIndex));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AntiCheatPatcher] Error patching calls in {method.FullName}: {ex}");
                    }
                }
            }

            Console.WriteLine("[AntiCheatPatcher] Preloader patching complete!");
        }
    }
}
