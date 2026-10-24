using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Reloaded.Hooks.Definitions;

namespace LivingWeapon.Tests;

/// <summary>
/// An IReloadedHooks whose hooks fire their detour once with default arguments inside Activate, the
/// way the game can call a function the instant a detour goes live. A detour that reads its own
/// hook field before the field is assigned throws a NullReferenceException out of Activate.
/// </summary>
internal class FiringHooks : DispatchProxy
{
    public readonly List<IFakeHook> Created = new();
    public bool Fire = true;
    /// <summary>1 based activation number that throws instead of firing, 0 for never.</summary>
    public int ThrowOnActivation;
    /// <summary>A throwing Activate leaves the hook reporting enabled, as a half done real one could.</summary>
    public bool EnabledWhenThrowing;
    public int Activations;
    /// <summary>1 based CreateHook call number that throws before a hook exists, 0 for never.</summary>
    public int ThrowOnCreate;
    public int Creates;
    /// <summary>Runs as the very first step of every Activate, so a test can look at the owner's state mid activation.</summary>
    public Action? OnActivate;

    public static FiringHooks Make(out IReloadedHooks hooks)
    {
        hooks = Create<IReloadedHooks, FiringHooks>();
        return (FiringHooks)(object)hooks;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "CreateHook" || args?[0] is not Delegate detour)
            throw new NotSupportedException(targetMethod?.Name);
        if (++Creates == ThrowOnCreate) throw new InvalidOperationException("create refused");
        var fake = (IFakeHook)Activator.CreateInstance(typeof(FakeHook<>).MakeGenericType(detour.GetType()), this, detour)!;
        Created.Add(fake);
        return fake;
    }

    public interface IFakeHook
    {
        bool Fired { get; }
        bool Activated { get; }
        int DisableCalls { get; }
        bool IsHookEnabled { get; }
    }

    internal sealed class FakeHook<T> : IHook<T>, IFakeHook where T : Delegate
    {
        private readonly FiringHooks _owner;
        private readonly T _detour;
        private bool _enabled;
        public bool Fired { get; private set; }
        public bool Activated { get; private set; }
        public int DisableCalls { get; private set; }

        public FakeHook(FiringHooks owner, T detour)
        {
            _owner = owner;
            _detour = detour;
            var invoke = typeof(T).GetMethod("Invoke")!;
            var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            OriginalFunction = Expression.Lambda<T>(Expression.Default(invoke.ReturnType), ps).Compile();
        }

        public T OriginalFunction { get; }
        public IReverseWrapper<T> ReverseWrapper => throw new NotSupportedException();
        public bool IsHookEnabled => _enabled;
        public bool IsHookActivated => Activated;
        public IntPtr OriginalFunctionAddress => IntPtr.Zero;
        public IntPtr OriginalFunctionWrapperAddress => IntPtr.Zero;

        public IHook<T> Activate()
        {
            _owner.OnActivate?.Invoke();
            int n = ++_owner.Activations;
            if (_owner.ThrowOnActivation == n)
            {
                _enabled = _owner.EnabledWhenThrowing;
                throw new InvalidOperationException("activate refused");
            }
            if (_owner.Fire)
            {
                Fired = true;
                var args = typeof(T).GetMethod("Invoke")!.GetParameters()
                    .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
                try { _detour.DynamicInvoke(args); }
                catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); }
            }
            _enabled = true;
            Activated = true;
            return this;
        }

        IHook IHook.Activate() => Activate();
        public void Disable() { DisableCalls++; _enabled = false; }
        public void Enable() { _enabled = true; }
    }
}
