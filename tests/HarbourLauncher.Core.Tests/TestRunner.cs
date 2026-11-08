using System.Reflection;

namespace HarbourLauncher.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string what) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'.");

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T e) { return e; }
        catch (Exception e) { throw new AssertionException($"Expected {typeof(T).Name}, got {e.GetType().Name}: {e.Message}"); }
        throw new AssertionException($"Expected {typeof(T).Name}, but nothing was thrown.");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T e) { return e; }
        catch (Exception e) { throw new AssertionException($"Expected {typeof(T).Name}, got {e.GetType().Name}: {e.Message}"); }
        throw new AssertionException($"Expected {typeof(T).Name}, but nothing was thrown.");
    }
}

/// <summary>
/// A dependency-free test runner, so tests run anywhere `dotnet run` works.
/// Swapping to xUnit later is mechanical: [Test] becomes [Fact].
/// </summary>
public static class Program
{
    public static async Task<int> Main()
    {
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();

        int failed = 0;
        foreach (var test in tests)
        {
            string name = $"{test.DeclaringType!.Name}.{test.Name}";
            try
            {
                if (test.Invoke(null, null) is Task task) await task;
                Console.WriteLine($"  pass  {name}");
            }
            catch (Exception e)
            {
                var error = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                Console.WriteLine($"  FAIL  {name}\n        {error.GetType().Name}: {error.Message}");
                failed++;
            }
        }

        Console.WriteLine($"\n{tests.Count - failed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
