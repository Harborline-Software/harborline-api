# Shared JS Interop Infrastructure — Implementation Notes

**Date:** 2026-04-09
**Scope:** Scaffold-only — no component features implemented

---

## What Was Scaffolded

A shared, reusable JS interop layer for complex Harborline components. This provides typed C# service abstractions, ES module JS implementations, and DI registration so that future component work (Window, Popover, DataGrid, Chart, etc.) can consume shared browser interop instead of each component reinventing its own.

### Services Created

| Interface                       | Implementation                | JS Module                      | Purpose                                              |
| ------------------------------- | ----------------------------- | ------------------------------ | ---------------------------------------------------- |
| `IHarborlineJsModuleLoader`        | `HarborlineJsModuleLoader`       | (orchestrator)                 | Lazy-loads and caches JS ES modules                  |
| `IElementMeasurementService`   | `ElementMeasurementService`  | `harborline-measurement.js`        | getBoundingClientRect, viewport measurement          |
| `IResizeObserverService`       | `ResizeObserverService`      | `harborline-observers.js`          | ResizeObserver wrapper with handle-based disposal    |
| `IIntersectionObserverService` | `IntersectionObserverService`| `harborline-observers.js`          | IntersectionObserver wrapper with handle-based disposal |
| `IPopupPositionService`        | `PopupPositionService`       | `harborline-positioning.js`        | Anchor-relative popup positioning with flip          |
| `IDragService`                 | `DragService`                | `harborline-dragdrop.js`           | Pointer-based drag operations                        |
| `IResizeInteractionService`    | `ResizeInteractionService`   | `harborline-resize.js`             | Pointer-based element resize operations              |
| `IClipboardService`            | `ClipboardService`           | `harborline-clipboard-download.js` | Clipboard read/write via Clipboard API               |
| `IDownloadService`             | `DownloadService`            | `harborline-clipboard-download.js` | In-memory file download triggering                   |
| `IGraphicsInteropService`      | `GraphicsInteropService`     | `harborline-graphics.js`           | Text measurement, DPI, element size for rendering    |

### DTOs and Enums

All defined in `InteropModels.cs`:
- `ElementRect`, `ViewportRect`
- `DragStartOptions`, `DragUpdate`, `DragResult`
- `ResizeHandle` (flags enum), `ResizeConstraints`, `ResizeUpdate`
- `PopupAnchorOptions`, `PopupPlacement` (enum), `PopupPositionResult`
- `IntersectionState`
- `DownloadRequest`, `ClipboardWriteRequest`

---

## Where It Lives

```
src/Harborline.Components.Blazor/
├── Internal/Interop/           ← C# interfaces, implementations, DTOs, DI registration
│   ├── I*.cs                   ← Service interfaces (internal)
│   ├── *.cs                    ← Service implementations (internal)
│   ├── InteropModels.cs        ← DTOs and enums (internal)
│   ├── InteropServiceExtensions.cs ← DI registration (public extension on HarborlineBuilder)
│   └── SHARED_INTEROP_NOTES.md ← This file
└── wwwroot/js/
    ├── harborline-measurement.js
    ├── harborline-observers.js
    ├── harborline-positioning.js
    ├── harborline-dragdrop.js
    ├── harborline-resize.js
    ├── harborline-clipboard-download.js
    └── harborline-graphics.js

tests/Harborline.Tests/Interop/
├── SharedInteropServiceTests.cs  ← DI registration, construction, disposal tests
├── InteropModelTests.cs          ← DTO/enum validation tests
└── JsModulePathTests.cs          ← JS file existence and export verification
```

---

## Which Components Should Consume Which Services

| Component         | Services                                                    |
| ----------------- | ----------------------------------------------------------- |
| **Window**        | `IDragService`, `IResizeInteractionService`, `IElementMeasurementService` |
| **Popover**       | `IPopupPositionService`, `IElementMeasurementService`       |
| **Splitter**      | `IResizeInteractionService`, `IElementMeasurementService`   |
| **DataGrid**      | `IResizeInteractionService`, `IElementMeasurementService`, `IIntersectionObserverService` (virtual scroll) |
| **TreeList**      | `IIntersectionObserverService` (virtual scroll)             |
| **PivotGrid**     | `IResizeInteractionService`, `IElementMeasurementService`   |
| **Scheduler**     | `IDragService`, `IResizeInteractionService`, `IElementMeasurementService` |
| **Gantt**         | `IDragService`, `IResizeInteractionService`, `IElementMeasurementService` |
| **Chart**         | `IGraphicsInteropService`, `IElementMeasurementService`, `IResizeObserverService` |
| **Diagram**       | `IGraphicsInteropService`, `IDragService`, `IElementMeasurementService` |
| **Map**           | `IGraphicsInteropService`, `IResizeObserverService`         |
| **Editor**        | `IClipboardService`, `IElementMeasurementService`           |
| **FileManager**   | `IClipboardService`, `IDownloadService`                     |
| **ResizableContainer** | `IResizeInteractionService`, `IResizeObserverService`  |

---

## What Was Intentionally Deferred

1. **Full feature wiring** — Services are injected into HarborlineWindow as a seam but not yet wired to replace the inline JS.
2. **Virtual scrolling service** — Deferred until DataGrid virtual scroll pass; may extend `IIntersectionObserverService` or be a separate service.
3. **Focus management service** — May be needed for modal trap, Popover focus; deferred until Popover/Dialog focus pass.
4. **DotNetObjectReference pooling** — The observer services create per-observation DotNetObjectReferences. Pooling may improve perf for high-frequency observe/unobserve.
5. **SSR/prerendering guards** — JS interop is not available during static SSR. Components already guard via `OnAfterRenderAsync`; the shared services rely on the same pattern.
6. **Keyboard resize/drag** — The shared drag/resize modules handle pointer events only. Keyboard-based resize (already in ResizableContainer's per-component JS) may be added later.

---

## Architecture Decisions

1. **Placement in `Internal/Interop`** rather than `Harborline.Api.Foundation`: The services depend on `IJSRuntime` and `IJSObjectReference` which are Blazor-specific. They also depend on the JS module files shipped with `Harborline.Components.Blazor`. Keeping them in Components ensures the JS files and C# wrappers ship together.

2. **Internal visibility**: All interfaces, DTOs, and implementations are `internal`. Only the `AddHarborlineInteropServices()` extension method is `public` (on `HarborlineBuilder`). Components inject services via `[Inject]` which bypasses access modifiers.

3. **Module loader pattern**: `HarborlineJsModuleLoader` centralizes all JS module imports with caching and disposal. This replaces the pattern where each component calls `JS.InvokeAsync<IJSObjectReference>("import", ...)` independently.

4. **Observer handle pattern**: `IResizeObserverService` and `IIntersectionObserverService` return `IAsyncDisposable` handles. Disposing the handle stops the observation. This prevents leaked observers.

5. **DI registration as explicit opt-in**: `AddHarborlineInteropServices()` is a separate call chained after `AddHarborline()` because providers don't reference `Harborline.Components.Blazor` (to avoid circular dependencies).

---

## Open Design Questions

1. Should `IHarborlineJsModuleLoader` be promoted to `Harborline.Api.Foundation` as an abstraction to allow testing without JS? Currently it's internal to Components.
2. Should the Window, Splitter, etc. migration from inline JS to shared modules happen incrementally (one service at a time) or as a batch?
3. Do we need a `IScrollService` for programmatic scroll (e.g., DataGrid scroll-to-row)?

---

## DI Registration

```csharp
// In Program.cs or startup:
builder.Services.AddHarborline()
    .AddHarborline()
    .AddHarborlineInteropServices();  // ← New
```

---

## Integration Seam

`HarborlineWindow.razor` now injects three shared services:
- `IElementMeasurementService`
- `IDragService`
- `IResizeInteractionService`

These are injected but **not yet wired** — the existing inline JS continues to handle drag/resize. The next pass will migrate Window to use these shared services.
