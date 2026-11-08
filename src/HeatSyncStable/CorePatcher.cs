// CorePatcher: applies the HeatSync-Stable IL fixes to the user's OWN installed GAMEPOWER.exe
// (GAMEPOWER HeatSync software 2.2.1.0, assembly "usb_hid", Dotfuscator-obfuscated) using dnlib.
// Obfuscated member names are written as \uXXXX escapes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace HeatSyncStable
{
    public static class CorePatcher
    {
        /// SHA-256 of the only supported original: GAMEPOWER.exe 2.2.1.0 (11.03.2025)
        public const string SupportedSha256 = "994498FE073B038E34D1318E577042B079823412BBCF5C2AC810E0CD03185E12";

        public static bool IsPatched(string exePath)
        {
            try
            {
                using (var m = ModuleDefMD.Load(File.ReadAllBytes(exePath)))
                    return m.GetAssemblyRefs().Any(r => r.Name == "HeatSyncFix");
            }
            catch { return false; }
        }

        public static List<string> Patch(string inPath, string outPath)
        {
            var log = new List<string>();
            var mod = ModuleDefMD.Load(File.ReadAllBytes(inPath));
            if (mod.Assembly == null || mod.Assembly.Name != "usb_hid") throw new InvalidDataException("Not a GAMEPOWER HeatSync executable (assembly usb_hid expected).");
            if (mod.GetAssemblyRefs().Any(r => r.Name == "HeatSyncFix")) throw new InvalidDataException("File is already patched.");

            TypeDef T(string name) => mod.GetTypes().Single(t => t.Name == name && t.DeclaringType == null);
            var fixAsm = new AssemblyRefUser("HeatSyncFix", new Version(1, 0, 0, 0));

            var serialBase = T("⃷");   // ⃷ : serial-port device base class
            var lcd = T("ℐ");          // ℐ : LCD device (USB35INCH)
            var mgr = T("℆");          // ℆ : sensor/display manager
            var x = T("x");                 // main form
            var write = serialBase.Methods.Single(m => m.Name == "ℚ" && m.Parameters.Count == 3
                            && m.Parameters[1].Type.FullName == "System.Byte[]" && m.Parameters[2].Type.FullName == "System.Boolean");
            var getPort = serialBase.Methods.Single(m => m.Name == "⅛" && m.Parameters.Count == 1);

            // 1) LCD arrows: bytes [10]/[11] of command 169 carried a free-running 0..7 counter -> temperature level;
            //    header fields 1 and 3 carried CPU load -> CPU temperature.
            {
                var loopType = x.NestedTypes.Single(n => n.Name == "ℸ");               // x/ℸ (variant-11 LCD loop)
                var mn = loopType.Methods.Single(m => m.Name == "⃼" && m.HasBody && m.Parameters.Count == 1);
                var cpuTemp = mgr.Methods.Single(m => m.Name == "⇝" && m.Parameters.Count == 1 && m.ReturnType.FullName == "System.Single");
                var gpuTemp = mgr.Methods.Single(m => m.Name == "⇶" && m.Parameters.Count == 1 && m.ReturnType.FullName == "System.Single");
                var cpuLoad = mgr.Methods.Single(m => m.Name == "⇢" && m.Parameters.Count == 1 && m.ReturnType.FullName == "System.Single");
                var counter = loopType.Fields.Single(f => f.Name == "℁" && f.FieldType.FullName == "System.Int32");
                var pkt = loopType.Fields.Single(f => f.Name == "ℋ" && f.FieldType.FullName == "System.Byte[]");
                var owner = loopType.Fields.Single(f => f.Name == "⃷");
                var lcdField = x.Fields.Single(f => f.Name == "ℐ" && f.FieldType.FullName == mgr.FullName);
                var arrowLevel = new MemberRefUser(mod, "ArrowLevel",
                    MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.Int32, mod.CorLibTypes.Single),
                    new TypeRefUser(mod, "HeatSyncFix", "Lcd", fixAsm));

                var b = mn.Body; b.SimplifyBranches();
                var ins = b.Instructions;
                var idx = Enumerable.Range(0, ins.Count).Where(i => ins[i].IsLdcI4() && ins[i].GetLdcI4Value() == 169).ToList();
                if (idx.Count != 1) throw new InvalidDataException("LCD packet (169) not found exactly once.");
                int idx169 = idx[0], replaced = 0;
                for (int i = Math.Max(0, idx169 - 40); i < idx169; i++)
                {
                    if (ins[i].OpCode == OpCodes.Ldfld && ins[i].Operand == pkt && ins[i + 1].IsLdcI4()
                        && (ins[i + 1].GetLdcI4Value() == 10 || ins[i + 1].GetLdcI4Value() == 11)
                        && ins[i + 2].OpCode == OpCodes.Ldarg_0 && ins[i + 3].OpCode == OpCodes.Ldfld && ins[i + 3].Operand == counter
                        && ins[i + 4].OpCode == OpCodes.Conv_U1 && ins[i + 5].OpCode == OpCodes.Stelem_I1)
                    {
                        int side = ins[i + 1].GetLdcI4Value() == 10 ? 0 : 1;
                        ins[i + 2].OpCode = OpCodes.Ldc_I4; ins[i + 2].Operand = side;
                        ins[i + 3].OpCode = OpCodes.Ldarg_0; ins[i + 3].Operand = null;
                        var add = new[] {
                            OpCodes.Ldfld.ToInstruction(owner), OpCodes.Ldfld.ToInstruction(lcdField),
                            OpCodes.Callvirt.ToInstruction(side == 0 ? cpuTemp : gpuTemp), OpCodes.Call.ToInstruction(arrowLevel) };
                        for (int k = 0; k < add.Length; k++) ins.Insert(i + 4 + k, add[k]);
                        replaced++; idx169 += add.Length;
                    }
                }
                if (replaced != 2) throw new InvalidDataException("Arrow counter stores not found (" + replaced + ").");
                int hdr = 0;
                for (int i = idx169; i < Math.Min(ins.Count, idx169 + 25); i++)
                {
                    if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is IMethod im && im.Name == cpuLoad.Name && im.DeclaringType.FullName == mgr.FullName)
                    { ins[i].Operand = cpuTemp; hdr++; }
                    if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is IMethod w && w.Name == "ℚ" && w.DeclaringType.FullName == lcd.FullName) break;
                }
                if (hdr != 2) throw new InvalidDataException("CPU-load header arguments not found (" + hdr + ").");
                b.OptimizeBranches();
                log.Add("arrows: bytes[10]/[11] -> HeatSyncFix.Lcd.ArrowLevel(CPU/GPU temp), header CPU load -> CPU temp");
            }

            // 2) HWiNFO "#216 update device" P/Invoke -> HeatSyncFix.Sensors.SafeUpdate (AccessViolation in nvml.dll)
            {
                var sensorType = T("⅌");
                var pinv = sensorType.Methods.Single(m => m.Name == "⃲" && m.IsPinvokeImpl && m.Parameters.Count == 1
                                                          && m.Parameters[0].Type.FullName == "System.Int32");
                if (pinv.ImplMap.Name != "#216") throw new InvalidDataException("Unexpected HWiNFO entry point " + pinv.ImplMap.Name);
                var safe = new MemberRefUser(mod, "SafeUpdate", MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.Int32),
                                             new TypeRefUser(mod, "HeatSyncFix", "Sensors", fixAsm));
                int n = 0;
                foreach (var t in mod.GetTypes()) foreach (var m in t.Methods.Where(m => m.HasBody))
                        foreach (var i in m.Body.Instructions)
                            if (i.OpCode == OpCodes.Call && i.Operand is MethodDef md && md == pinv) { i.Operand = safe; n++; }
                if (n == 0) throw new InvalidDataException("HWiNFO update call sites not found.");
                log.Add("hwinfo: " + n + " call sites -> HeatSyncFix.Sensors.SafeUpdate");
            }

            // 3) hidden developer button "button1" (新主题, TabIndex 0) -> no-op
            {
                var h = x.Methods.Single(m => m.Name == "⇬" && m.Parameters.Count == 3);
                var body = new CilBody(); body.Instructions.Add(OpCodes.Ret.ToInstruction()); h.Body = body;
                log.Add("dev button handler -> no-op");
            }

            // 4) serial write: optional protocol logger + guard against a null/closed port (was NullReferenceException)
            {
                var b = write.Body; b.SimplifyBranches();
                var onWrite = new MemberRefUser(mod, "OnWrite",
                    MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.Object, new SZArraySig(mod.CorLibTypes.Byte), mod.CorLibTypes.Boolean),
                    new TypeRefUser(mod, "HeatSyncFix", "Diag", fixAsm));
                var hook = new[] { OpCodes.Ldarg_0.ToInstruction(), OpCodes.Ldarg_1.ToInstruction(), OpCodes.Ldarg_2.ToInstruction(), OpCodes.Call.ToInstruction(onWrite) };
                for (int i = 0; i < hook.Length; i++) b.Instructions.Insert(i, hook[i]);

                var portTypeRef = getPort.ReturnType.ToTypeDefOrRef();
                var isOpenRef = new MemberRefUser(mod, "get_IsOpen", MethodSig.CreateInstance(mod.CorLibTypes.Boolean), portTypeRef);
                var ioexType = new TypeRefUser(mod, "System.IO", "IOException", mod.CorLibTypes.AssemblyRef);
                var ioexCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.String), ioexType);
                var first = b.Instructions[0];
                var thr = OpCodes.Ldstr.ToInstruction("LCD serial port is not open (device not connected yet)");
                var guard = new[] {
                    OpCodes.Ldarg_0.ToInstruction(), OpCodes.Call.ToInstruction(getPort), OpCodes.Brfalse.ToInstruction(thr),
                    OpCodes.Ldarg_0.ToInstruction(), OpCodes.Call.ToInstruction(getPort), OpCodes.Callvirt.ToInstruction(isOpenRef),
                    OpCodes.Brtrue.ToInstruction(first), thr, OpCodes.Newobj.ToInstruction(ioexCtor), OpCodes.Throw.ToInstruction() };
                for (int i = 0; i < guard.Length; i++) b.Instructions.Insert(i, guard[i]);
                b.OptimizeBranches();
                log.Add("serial write: null/closed port guard + optional logger");
            }

            var wo = new ModuleWriterOptions(mod);
            wo.MetadataOptions.Flags |= MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack;
            wo.Logger = DummyLogger.NoThrowInstance;
            var tmp = outPath + ".tmp";
            mod.Write(tmp, wo);
            mod.Dispose();
            if (File.Exists(outPath)) { File.SetAttributes(outPath, System.IO.FileAttributes.Normal); File.Delete(outPath); }
            File.Move(tmp, outPath);
            return log;
        }
    }
}
