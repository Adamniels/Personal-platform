using System.Reflection;
using System.Reflection.Emit;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// Finds which methods of an assembly call a given constructor, by reading their compiled IL. A
/// test pins where a value can be made with it, which a search of the source would miss for a
/// target typed <c>new()</c>. Only the operand of a call or <c>newobj</c> is resolved; every other
/// instruction is stepped over by its operand's size.
/// </summary>
internal static class IlScan
{
    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>Every method or constructor in <paramref name="assembly"/>, compiler generated ones
    /// included, whose body constructs or calls one of <paramref name="targets"/>.</summary>
    public static IEnumerable<MethodBase> MethodsCalling(Assembly assembly, IReadOnlyCollection<MethodBase> targets)
    {
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
            {
                if (method.GetMethodBody()?.GetILAsByteArray() is { } il && Calls(method, il, targets))
                {
                    yield return method;
                }
            }
        }
    }

    /// <summary>The type a nested one sits in at the top, so a lambda's or an async method's
    /// generated class counts as the type that wrote it.</summary>
    public static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    private static bool Calls(MethodBase method, byte[] il, IReadOnlyCollection<MethodBase> targets)
    {
        var at = 0;
        while (at < il.Length)
        {
            short value = il[at] == 0xFE ? (short)(0xFE00 | il[at + 1]) : il[at];
            at += il[at] == 0xFE ? 2 : 1;
            var code = OpCodesByValue[value];
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, at);
                var called = method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (targets.Contains(called))
                {
                    return true;
                }
            }

            at += OperandSize(code, il, at);
        }

        return false;
    }

    private static int OperandSize(OpCode code, byte[] il, int at) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
        _ => 4,
    };
}