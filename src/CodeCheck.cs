using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace GapCloser
{
    // a short print of a method's own code: its instructions with every operand written by name, branches as instruction
    // numbers. other mods' patches, build numbers and assembly versions do not change it, a change of the code does
    internal static class CodeCheck
    {
        // the first 8 hex digits of SHA256 over the written code, null when the code can not be read
        internal static string Of(MethodBase method)
        {
            if (method == null) return null;
            List<CodeInstruction> code;
            try
            {
                code = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
            }
            catch
            {
                return null;
            }
            var at = new Dictionary<Label, int>();
            for (int i = 0; i < code.Count; i++)
                foreach (var label in code[i].labels)
                    at[label] = i;
            var text = new StringBuilder();
            foreach (var c in code)
            {
                text.Append(c.opcode.Name).Append(' ').Append(Operand(c.operand, at));
                foreach (var block in c.blocks) text.Append(" [").Append(block.blockType).Append(']');
                text.Append('\n');
            }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                return BitConverter.ToString(hash, 0, 4).Replace("-", "");
            }
        }

        private static string Operand(object operand, Dictionary<Label, int> at)
        {
            switch (operand)
            {
                case null: return "";
                case Label label: return Target(label, at);
                case Label[] labels:
                    var targets = new string[labels.Length];
                    for (int i = 0; i < labels.Length; i++) targets[i] = Target(labels[i], at);
                    return string.Join(",", targets);
                case LocalBuilder local: return "loc" + local.LocalIndex + ":" + local.LocalType;
                case MethodBase method:
                    var parameters = method.GetParameters();
                    var types = new string[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++) types[i] = parameters[i].ParameterType.ToString();
                    return method.DeclaringType + "::" + method.Name + "(" + string.Join(",", types) + ")";
                case FieldInfo field: return field.DeclaringType + "::" + field.Name;
                case Type type: return type.ToString();
                case IConvertible value: return Convert.ToString(value, CultureInfo.InvariantCulture);
                default: return operand.ToString();
            }
        }

        private static string Target(Label label, Dictionary<Label, int> at) => at.TryGetValue(label, out int i) ? "@" + i : "@?";
    }
}
