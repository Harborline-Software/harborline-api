using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Harborline.Api.UIAdapters.Blazor.Internal.Interop;

namespace Harborline.Api.UIAdapters.Blazor.FormFactor;

/// <summary>
/// JS-interop-backed <see cref="IFormFactorService"/>. Wires <c>harborline-form-factor.js</c>'s
/// generic matchMedia bridge to the contract's own named queries (passed AS PARAMETERS, never
/// hard-coded in JS — see the module's header comment) and resolves matched state via
/// <see cref="FormFactorResolver.Resolve"/>, the SAME pure resolver the generated TS runtime uses.
/// </summary>
/// <remarks>
/// Register with <c>AddHarborlineFormFactor()</c> (scoped — one instance per Blazor circuit/tab) and
/// consume via <see cref="FormFactorProvider"/>, which owns the <see cref="InitializeAsync"/> call
/// and cascades this service to descendants.
/// </remarks>
public sealed class FormFactorService : IFormFactorService, IAsyncDisposable
{
    private static readonly string ModuleUri =
        HarborlineJsModuleLoader.Resolve("js/harborline-form-factor.js");

    private readonly IJSRuntime _js;
    private Task<IJSObjectReference>? _moduleTask;
    private DotNetObjectReference<FormFactorService>? _selfRef;
    private bool _initializing;

    public FormFactorService(IJSRuntime js) => _js = js;

    public ResolvedFormFactor Current { get; private set; } = FormFactorResolver.Resolve(default);

    public bool IsInitialized { get; private set; }

    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (IsInitialized || _initializing) return;
        _initializing = true;
        try
        {
            var module = await EnsureModuleAsync().ConfigureAwait(false);
            _selfRef ??= DotNetObjectReference.Create(this);
            var initial = await module.InvokeAsync<JsMatchedMediaState>(
                "watch", CancellationToken.None, _selfRef, QueryMap()).ConfigureAwait(false);
            ApplyState(initial);
            IsInitialized = true;
            Changed?.Invoke();
        }
        catch (JSDisconnectedException) { /* circuit already gone */ }
        catch (JSException)             { /* module load / matchMedia failure */ }
        catch (TaskCanceledException)   { /* navigation or disposal mid-flight */ }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>Invoked by harborline-form-factor.js whenever any tracked query's match state changes.</summary>
    [JSInvokable]
    public void OnFormFactorChanged(JsMatchedMediaState state)
    {
        ApplyState(state);
        Changed?.Invoke();
    }

    private void ApplyState(JsMatchedMediaState js) => Current = FormFactorResolver.Resolve(new MatchedMediaState(
        PhoneWidth: js.PhoneWidth,
        DesktopWidth: js.DesktopWidth,
        Landscape: js.Landscape,
        ShortHeight: js.ShortHeight,
        PointerCoarse: js.PointerCoarse,
        AnyCoarse: js.AnyCoarse,
        AnyFine: js.AnyFine,
        Hover: js.Hover));

    /// <summary>
    /// The query map handed to the JS module — camelCase keys matching <see cref="JsMatchedMediaState"/>'s
    /// interop (de)serialization, values sourced from <see cref="FormFactorModeQueries"/> /
    /// <see cref="FormFactorSubSignalQueries"/> / <see cref="FormFactorCapabilityQueries"/> (the
    /// generated contract constants) — never a literal breakpoint here.
    /// </summary>
    private static object QueryMap() => new
    {
        phoneWidth = FormFactorModeQueries.Phone,
        desktopWidth = FormFactorModeQueries.Desktop,
        landscape = FormFactorSubSignalQueries.OrientationLandscape,
        shortHeight = FormFactorSubSignalQueries.HeightShort,
        pointerCoarse = FormFactorCapabilityQueries.PointerCoarse,
        anyCoarse = FormFactorCapabilityQueries.AnyCoarse,
        anyFine = FormFactorCapabilityQueries.AnyFine,
        hover = FormFactorCapabilityQueries.Hover,
    };

    private ValueTask<IJSObjectReference> EnsureModuleAsync()
    {
        _moduleTask ??= _js.InvokeAsync<IJSObjectReference>("import", ModuleUri).AsTask();
        return new ValueTask<IJSObjectReference>(_moduleTask);
    }

    public async ValueTask DisposeAsync()
    {
        _selfRef?.Dispose();
        if (_moduleTask is not null)
        {
            try
            {
                var module = await _moduleTask.ConfigureAwait(false);
                await module.InvokeVoidAsync("unwatch").ConfigureAwait(false);
                await module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException) { /* circuit already gone */ }
            catch (JSException)             { /* module never loaded */  }
        }
    }
}

/// <summary>
/// The wire shape harborline-form-factor.js sends/returns — one boolean per named query, matching
/// <see cref="MatchedMediaState"/> field-for-field (Blazor's default JS interop JSON options use a
/// camelCase naming policy, so this PascalCase record deserializes the JS module's camelCase object
/// without any explicit attributes).
/// </summary>
public readonly record struct JsMatchedMediaState(
    bool PhoneWidth,
    bool DesktopWidth,
    bool Landscape,
    bool ShortHeight,
    bool PointerCoarse,
    bool AnyCoarse,
    bool AnyFine,
    bool Hover);
