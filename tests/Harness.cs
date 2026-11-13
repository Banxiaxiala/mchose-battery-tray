// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
//
// 1. [极小测试宿主] 不用第三方测试框架，自己 60 行足够。
//    Main 返回 0 = 全过，非 0 = 有失败，可直接用于 CI。
// 2. [不捕获致命异常] 断言失败只记录并继续跑完，最后统一汇总 ——
//    否则第一条失败就中断，后面的错误要反复跑才发现。
// 3. [编码] UTF-8 with BOM。

using System;
using System.Collections.Generic;
using System.Reflection;

namespace PeripheralBattery.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public sealed class AssertFailedException : Exception
    {
        public AssertFailedException(string message) : base(message) { }
    }

    public static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new AssertFailedException(message);
        }

        public static void False(bool condition, string message)
        {
            if (condition) throw new AssertFailedException(message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new AssertFailedException(
                    $"{message}：期望 {expected}，实际 {actual}");
            }
        }

        public static void Null(object? value, string message)
        {
            if (value != null) throw new AssertFailedException($"{message}：期望 null，实际 {value}");
        }

        public static void NotNull(object? value, string message)
        {
            if (value == null) throw new AssertFailedException($"{message}：期望非 null");
        }

        public static void Close(double expected, double actual, double tolerance,
            string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new AssertFailedException(
                    $"{message}：期望 {expected}（±{tolerance}），实际 {actual}");
            }
        }
    }

    public static class TestRunner
    {
        public static int Run()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (Exception) { }

            int passed = 0;
            var failures = new List<string>();

            var types = Assembly.GetExecutingAssembly().GetTypes();
            Array.Sort(types, (a, b) => string.CompareOrdinal(a.FullName, b.FullName));

            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                Array.Sort(methods, (a, b) => string.CompareOrdinal(a.Name, b.Name));

                foreach (var method in methods)
                {
                    if (method.GetCustomAttribute<TestAttribute>() == null) continue;

                    string name = type.Name + "." + method.Name;
                    try
                    {
                        method.Invoke(null, null);
                        passed++;
                        Console.WriteLine("  PASS  " + name);
                    }
                    catch (TargetInvocationException wrap)
                    {
                        string reason = wrap.InnerException?.Message ?? "未知错误";
                        failures.Add(name + " → " + reason);
                        Console.WriteLine("  FAIL  " + name);
                        Console.WriteLine("        " + reason);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"共 {passed + failures.Count} 项，通过 {passed}，失败 {failures.Count}");
            if (failures.Count > 0)
            {
                Console.WriteLine();
                foreach (string failure in failures) Console.WriteLine("  × " + failure);
            }
            return failures.Count == 0 ? 0 : 1;
        }
    }

    internal static class Program
    {
        private static int Main() { return TestRunner.Run(); }
    }
}
