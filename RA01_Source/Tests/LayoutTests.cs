using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - THE SHAPES HANDED TO OPENXR, D3D12, WINDOWS AND XINPUT. Each struct in Core\Video\VRRuntimes\OpenXR.cs (Xr), Core\Win32.cs and
// Core\controls_mapping\ControllerMapping.cs must be byte-for-byte the shape the other side expects: one field out of place and the headset, the
// graphics card or the game reads garbage. The numbers below were NOT taken from the mod: they were measured from the official headers (Khronos
// openxr.h / openxr_platform.h, and the Windows SDK headers d3d12.h, dxgi1_2.h, tlhelp32.h, xinput.h, winuser.h) compiled for 64-bit Windows.
// Builder picture: checking a part against the manufacturer's spec sheet with calipers, not against the last part we made.
// Cost - CPU: extremely low (about 300 size and offset reads); GPU: none.
public class LayoutTests
{
    // RA01: sizeof() of each struct, from the official headers (Windows x64).
    static readonly Dictionary<string, int> Size = new()
    {
        ["Xr.Quaternionf"] = 16, ["Xr.Vector3f"] = 12, ["Xr.Posef"] = 28, ["Xr.Fovf"] = 16, ["Xr.Offset2Di"] = 8, ["Xr.Extent2Di"] = 8, ["Xr.Rect2Di"] = 16,
        ["Xr.Extent2Df"] = 8, ["Xr.Vector2f"] = 8, ["Xr.ApplicationInfo"] = 272, ["Xr.InstanceCreateInfo"] = 328, ["Xr.InstanceProperties"] = 152, ["Xr.ExtensionProperties"] = 152, ["Xr.InteractionProfileState"] = 24,
        ["Xr.SystemGetInfo"] = 24, ["Xr.SystemProperties"] = 304, ["Xr.SessionCreateInfo"] = 32, ["Xr.GraphicsBindingD3D12"] = 32,
        ["Xr.GraphicsRequirementsD3D12"] = 32, ["Xr.ReferenceSpaceCreateInfo"] = 48, ["Xr.EventDataBuffer"] = 4016, ["Xr.EventDataSessionStateChanged"] = 40,
        ["Xr.EventDataReferenceSpaceChangePending"] = 72, ["Xr.SessionBeginInfo"] = 24, ["Xr.FrameWaitInfo"] = 16, ["Xr.FrameState"] = 40,
        ["Xr.FrameBeginInfo"] = 16, ["Xr.FrameEndInfo"] = 40, ["Xr.ViewLocateInfo"] = 40, ["Xr.ViewState"] = 24, ["Xr.View"] = 64, ["Xr.SpaceLocation"] = 56,
        ["Xr.SwapchainCreateInfo"] = 64, ["Xr.SwapchainImageD3D12"] = 24, ["Xr.SwapchainImageAcquireInfo"] = 16, ["Xr.SwapchainImageWaitInfo"] = 24,
        ["Xr.SwapchainImageReleaseInfo"] = 16, ["Xr.SwapchainSubImage"] = 32, ["Xr.CompositionLayerQuad"] = 112, ["Xr.CompositionLayerProjectionView"] = 96,
        ["Xr.CompositionLayerProjection"] = 48, ["Xr.ViewConfigurationView"] = 40, ["Xr.ActionSetCreateInfo"] = 216, ["Xr.ActionCreateInfo"] = 224,
        ["Xr.ActionSuggestedBinding"] = 16, ["Xr.InteractionProfileSuggestedBinding"] = 40, ["Xr.SessionActionSetsAttachInfo"] = 32, ["Xr.ActiveActionSet"] = 16,
        ["Xr.ActionsSyncInfo"] = 32, ["Xr.ActionStateGetInfo"] = 32, ["Xr.ActionStateBoolean"] = 40, ["Xr.ActionStateFloat"] = 40, ["Xr.ActionStateVector2f"] = 48,
        ["Xr.ActionStatePose"] = 24, ["Xr.ActionSpaceCreateInfo"] = 64, ["Win32.CommandQueueDesc"] = 16, ["Win32.SwapChainDesc1"] = 48, ["Win32.SwapChainDesc"] = 72,
        ["Win32.HeapProperties"] = 20, ["Win32.ResourceDesc"] = 56, ["Win32.ResourceBarrier"] = 32, ["Win32.MemoryBasicInformation"] = 48,
        ["Win32.ThreadEntry"] = 28, ["Win32.Rect"] = 16, ["Win32.Point"] = 8, ["Win32.CursorInfo"] = 24, ["Win32.BitmapInfoHeader"] = 40,
        ["Win32.TextureCopyLocation"] = 48, ["ControllerMapping.Gamepad"] = 12, ["ControllerMapping.State"] = 16,
    };

    // RA01: offsetof() of each OpenXR struct's fields, from openxr.h (Windows x64).
    static readonly Dictionary<string, int> Offset = new()
    {
        ["Xr.Quaternionf.X"] = 0, ["Xr.Quaternionf.Y"] = 4, ["Xr.Quaternionf.Z"] = 8, ["Xr.Quaternionf.W"] = 12, ["Xr.Vector3f.X"] = 0, ["Xr.Vector3f.Y"] = 4,
        ["Xr.Vector3f.Z"] = 8, ["Xr.Posef.Orientation"] = 0, ["Xr.Posef.Position"] = 16, ["Xr.Fovf.AngleLeft"] = 0, ["Xr.Fovf.AngleRight"] = 4,
        ["Xr.Fovf.AngleUp"] = 8, ["Xr.Fovf.AngleDown"] = 12, ["Xr.Offset2Di.X"] = 0, ["Xr.Offset2Di.Y"] = 4, ["Xr.Extent2Di.Width"] = 0, ["Xr.Extent2Di.Height"] = 4,
        ["Xr.Rect2Di.Offset"] = 0, ["Xr.Rect2Di.Extent"] = 8, ["Xr.Extent2Df.Width"] = 0, ["Xr.Extent2Df.Height"] = 4, ["Xr.Vector2f.X"] = 0, ["Xr.Vector2f.Y"] = 4,
        ["Xr.ApplicationInfo.ApplicationName"] = 0, ["Xr.ApplicationInfo.ApplicationVersion"] = 128, ["Xr.ApplicationInfo.EngineName"] = 132,
        ["Xr.ApplicationInfo.EngineVersion"] = 260, ["Xr.ApplicationInfo.ApiVersion"] = 264, ["Xr.InstanceCreateInfo.Type"] = 0, ["Xr.InstanceCreateInfo.Next"] = 8,
        ["Xr.InstanceCreateInfo.CreateFlags"] = 16, ["Xr.InstanceCreateInfo.ApplicationInfo"] = 24, ["Xr.InstanceCreateInfo.EnabledApiLayerCount"] = 296,
        ["Xr.InstanceCreateInfo.EnabledApiLayerNames"] = 304, ["Xr.InstanceCreateInfo.EnabledExtensionCount"] = 312,
        ["Xr.InstanceCreateInfo.EnabledExtensionNames"] = 320, ["Xr.InstanceProperties.Type"] = 0, ["Xr.InstanceProperties.Next"] = 8,
        ["Xr.InstanceProperties.RuntimeVersion"] = 16, ["Xr.InstanceProperties.RuntimeName"] = 24, ["Xr.ExtensionProperties.Type"] = 0, ["Xr.ExtensionProperties.Next"] = 8, ["Xr.ExtensionProperties.ExtensionName"] = 16, ["Xr.ExtensionProperties.ExtensionVersion"] = 144, ["Xr.InteractionProfileState.Type"] = 0, ["Xr.InteractionProfileState.Next"] = 8, ["Xr.InteractionProfileState.InteractionProfile"] = 16, ["Xr.SystemGetInfo.Type"] = 0, ["Xr.SystemGetInfo.Next"] = 8,
        ["Xr.SystemGetInfo.FormFactor"] = 16, ["Xr.SystemProperties.Type"] = 0, ["Xr.SystemProperties.Next"] = 8, ["Xr.SystemProperties.SystemId"] = 16,
        ["Xr.SystemProperties.VendorId"] = 24, ["Xr.SystemProperties.SystemName"] = 28, ["Xr.SystemProperties.MaxSwapchainImageHeight"] = 284,
        ["Xr.SystemProperties.MaxSwapchainImageWidth"] = 288, ["Xr.SystemProperties.MaxLayerCount"] = 292, ["Xr.SystemProperties.OrientationTracking"] = 296,
        ["Xr.SystemProperties.PositionTracking"] = 300, ["Xr.SessionCreateInfo.Type"] = 0, ["Xr.SessionCreateInfo.Next"] = 8,
        ["Xr.SessionCreateInfo.CreateFlags"] = 16, ["Xr.SessionCreateInfo.SystemId"] = 24, ["Xr.GraphicsBindingD3D12.Type"] = 0,
        ["Xr.GraphicsBindingD3D12.Next"] = 8, ["Xr.GraphicsBindingD3D12.Device"] = 16, ["Xr.GraphicsBindingD3D12.Queue"] = 24,
        ["Xr.GraphicsRequirementsD3D12.Type"] = 0, ["Xr.GraphicsRequirementsD3D12.Next"] = 8, ["Xr.GraphicsRequirementsD3D12.AdapterLuidLowPart"] = 16,
        ["Xr.GraphicsRequirementsD3D12.AdapterLuidHighPart"] = 20, ["Xr.GraphicsRequirementsD3D12.MinFeatureLevel"] = 24, ["Xr.ReferenceSpaceCreateInfo.Type"] = 0,
        ["Xr.ReferenceSpaceCreateInfo.Next"] = 8, ["Xr.ReferenceSpaceCreateInfo.ReferenceSpaceType"] = 16, ["Xr.ReferenceSpaceCreateInfo.PoseInReferenceSpace"] = 20,
        ["Xr.EventDataBuffer.Type"] = 0, ["Xr.EventDataBuffer.Next"] = 8, ["Xr.EventDataBuffer.Varying"] = 16, ["Xr.EventDataSessionStateChanged.Type"] = 0,
        ["Xr.EventDataSessionStateChanged.Next"] = 8, ["Xr.EventDataSessionStateChanged.Session"] = 16, ["Xr.EventDataSessionStateChanged.State"] = 24,
        ["Xr.EventDataSessionStateChanged.Time"] = 32, ["Xr.EventDataReferenceSpaceChangePending.Type"] = 0, ["Xr.EventDataReferenceSpaceChangePending.Next"] = 8,
        ["Xr.EventDataReferenceSpaceChangePending.Session"] = 16, ["Xr.EventDataReferenceSpaceChangePending.ReferenceSpaceType"] = 24,
        ["Xr.EventDataReferenceSpaceChangePending.ChangeTime"] = 32, ["Xr.EventDataReferenceSpaceChangePending.PoseValid"] = 40,
        ["Xr.EventDataReferenceSpaceChangePending.PoseInPreviousSpace"] = 44, ["Xr.SessionBeginInfo.Type"] = 0, ["Xr.SessionBeginInfo.Next"] = 8,
        ["Xr.SessionBeginInfo.PrimaryViewConfigurationType"] = 16, ["Xr.FrameWaitInfo.Type"] = 0, ["Xr.FrameWaitInfo.Next"] = 8, ["Xr.FrameState.Type"] = 0,
        ["Xr.FrameState.Next"] = 8, ["Xr.FrameState.PredictedDisplayTime"] = 16, ["Xr.FrameState.PredictedDisplayPeriod"] = 24, ["Xr.FrameState.ShouldRender"] = 32,
        ["Xr.FrameBeginInfo.Type"] = 0, ["Xr.FrameBeginInfo.Next"] = 8, ["Xr.FrameEndInfo.Type"] = 0, ["Xr.FrameEndInfo.Next"] = 8,
        ["Xr.FrameEndInfo.DisplayTime"] = 16, ["Xr.FrameEndInfo.EnvironmentBlendMode"] = 24, ["Xr.FrameEndInfo.LayerCount"] = 28, ["Xr.FrameEndInfo.Layers"] = 32,
        ["Xr.ViewLocateInfo.Type"] = 0, ["Xr.ViewLocateInfo.Next"] = 8, ["Xr.ViewLocateInfo.ViewConfigurationType"] = 16, ["Xr.ViewLocateInfo.DisplayTime"] = 24,
        ["Xr.ViewLocateInfo.Space"] = 32, ["Xr.ViewState.Type"] = 0, ["Xr.ViewState.Next"] = 8, ["Xr.ViewState.ViewStateFlags"] = 16, ["Xr.View.Type"] = 0,
        ["Xr.View.Next"] = 8, ["Xr.View.Pose"] = 16, ["Xr.View.Fov"] = 44, ["Xr.SpaceLocation.Type"] = 0, ["Xr.SpaceLocation.Next"] = 8,
        ["Xr.SpaceLocation.LocationFlags"] = 16, ["Xr.SpaceLocation.Pose"] = 24, ["Xr.SwapchainCreateInfo.Type"] = 0, ["Xr.SwapchainCreateInfo.Next"] = 8,
        ["Xr.SwapchainCreateInfo.CreateFlags"] = 16, ["Xr.SwapchainCreateInfo.UsageFlags"] = 24, ["Xr.SwapchainCreateInfo.Format"] = 32,
        ["Xr.SwapchainCreateInfo.SampleCount"] = 40, ["Xr.SwapchainCreateInfo.Width"] = 44, ["Xr.SwapchainCreateInfo.Height"] = 48,
        ["Xr.SwapchainCreateInfo.FaceCount"] = 52, ["Xr.SwapchainCreateInfo.ArraySize"] = 56, ["Xr.SwapchainCreateInfo.MipCount"] = 60,
        ["Xr.SwapchainImageD3D12.Type"] = 0, ["Xr.SwapchainImageD3D12.Next"] = 8, ["Xr.SwapchainImageD3D12.Texture"] = 16, ["Xr.SwapchainImageAcquireInfo.Type"] = 0,
        ["Xr.SwapchainImageAcquireInfo.Next"] = 8, ["Xr.SwapchainImageWaitInfo.Type"] = 0, ["Xr.SwapchainImageWaitInfo.Next"] = 8,
        ["Xr.SwapchainImageWaitInfo.Timeout"] = 16, ["Xr.SwapchainImageReleaseInfo.Type"] = 0, ["Xr.SwapchainImageReleaseInfo.Next"] = 8,
        ["Xr.SwapchainSubImage.Swapchain"] = 0, ["Xr.SwapchainSubImage.ImageRect"] = 8, ["Xr.SwapchainSubImage.ImageArrayIndex"] = 24,
        ["Xr.CompositionLayerQuad.Type"] = 0, ["Xr.CompositionLayerQuad.Next"] = 8, ["Xr.CompositionLayerQuad.LayerFlags"] = 16,
        ["Xr.CompositionLayerQuad.Space"] = 24, ["Xr.CompositionLayerQuad.EyeVisibility"] = 32, ["Xr.CompositionLayerQuad.SubImage"] = 40,
        ["Xr.CompositionLayerQuad.Pose"] = 72, ["Xr.CompositionLayerQuad.Size"] = 100, ["Xr.CompositionLayerProjectionView.Type"] = 0,
        ["Xr.CompositionLayerProjectionView.Next"] = 8, ["Xr.CompositionLayerProjectionView.Pose"] = 16, ["Xr.CompositionLayerProjectionView.Fov"] = 44,
        ["Xr.CompositionLayerProjectionView.SubImage"] = 64, ["Xr.CompositionLayerProjection.Type"] = 0, ["Xr.CompositionLayerProjection.Next"] = 8,
        ["Xr.CompositionLayerProjection.LayerFlags"] = 16, ["Xr.CompositionLayerProjection.Space"] = 24, ["Xr.CompositionLayerProjection.ViewCount"] = 32,
        ["Xr.CompositionLayerProjection.Views"] = 40, ["Xr.ViewConfigurationView.Type"] = 0, ["Xr.ViewConfigurationView.Next"] = 8,
        ["Xr.ViewConfigurationView.RecommendedImageRectWidth"] = 16, ["Xr.ViewConfigurationView.MaxImageRectWidth"] = 20,
        ["Xr.ViewConfigurationView.RecommendedImageRectHeight"] = 24, ["Xr.ViewConfigurationView.MaxImageRectHeight"] = 28,
        ["Xr.ViewConfigurationView.RecommendedSwapchainSampleCount"] = 32, ["Xr.ViewConfigurationView.MaxSwapchainSampleCount"] = 36,
        ["Xr.ActionSetCreateInfo.Type"] = 0, ["Xr.ActionSetCreateInfo.Next"] = 8, ["Xr.ActionSetCreateInfo.ActionSetName"] = 16,
        ["Xr.ActionSetCreateInfo.LocalizedActionSetName"] = 80, ["Xr.ActionSetCreateInfo.Priority"] = 208, ["Xr.ActionCreateInfo.Type"] = 0,
        ["Xr.ActionCreateInfo.Next"] = 8, ["Xr.ActionCreateInfo.ActionName"] = 16, ["Xr.ActionCreateInfo.ActionType"] = 80,
        ["Xr.ActionCreateInfo.CountSubactionPaths"] = 84, ["Xr.ActionCreateInfo.SubactionPaths"] = 88, ["Xr.ActionCreateInfo.LocalizedActionName"] = 96,
        ["Xr.ActionSuggestedBinding.Action"] = 0, ["Xr.ActionSuggestedBinding.Binding"] = 8, ["Xr.InteractionProfileSuggestedBinding.Type"] = 0,
        ["Xr.InteractionProfileSuggestedBinding.Next"] = 8, ["Xr.InteractionProfileSuggestedBinding.InteractionProfile"] = 16,
        ["Xr.InteractionProfileSuggestedBinding.CountSuggestedBindings"] = 24, ["Xr.InteractionProfileSuggestedBinding.SuggestedBindings"] = 32,
        ["Xr.SessionActionSetsAttachInfo.Type"] = 0, ["Xr.SessionActionSetsAttachInfo.Next"] = 8, ["Xr.SessionActionSetsAttachInfo.CountActionSets"] = 16,
        ["Xr.SessionActionSetsAttachInfo.ActionSets"] = 24, ["Xr.ActiveActionSet.ActionSet"] = 0, ["Xr.ActiveActionSet.SubactionPath"] = 8,
        ["Xr.ActionsSyncInfo.Type"] = 0, ["Xr.ActionsSyncInfo.Next"] = 8, ["Xr.ActionsSyncInfo.CountActiveActionSets"] = 16,
        ["Xr.ActionsSyncInfo.ActiveActionSets"] = 24, ["Xr.ActionStateGetInfo.Type"] = 0, ["Xr.ActionStateGetInfo.Next"] = 8, ["Xr.ActionStateGetInfo.Action"] = 16,
        ["Xr.ActionStateGetInfo.SubactionPath"] = 24, ["Xr.ActionStateBoolean.Type"] = 0, ["Xr.ActionStateBoolean.Next"] = 8,
        ["Xr.ActionStateBoolean.CurrentState"] = 16, ["Xr.ActionStateBoolean.ChangedSinceLastSync"] = 20, ["Xr.ActionStateBoolean.LastChangeTime"] = 24,
        ["Xr.ActionStateBoolean.IsActive"] = 32, ["Xr.ActionStateFloat.Type"] = 0, ["Xr.ActionStateFloat.Next"] = 8, ["Xr.ActionStateFloat.CurrentState"] = 16,
        ["Xr.ActionStateFloat.ChangedSinceLastSync"] = 20, ["Xr.ActionStateFloat.LastChangeTime"] = 24, ["Xr.ActionStateFloat.IsActive"] = 32,
        ["Xr.ActionStateVector2f.Type"] = 0, ["Xr.ActionStateVector2f.Next"] = 8, ["Xr.ActionStateVector2f.CurrentState"] = 16,
        ["Xr.ActionStateVector2f.ChangedSinceLastSync"] = 24, ["Xr.ActionStateVector2f.LastChangeTime"] = 32, ["Xr.ActionStateVector2f.IsActive"] = 40,
        ["Xr.ActionStatePose.Type"] = 0, ["Xr.ActionStatePose.Next"] = 8, ["Xr.ActionStatePose.IsActive"] = 16, ["Xr.ActionSpaceCreateInfo.Type"] = 0,
        ["Xr.ActionSpaceCreateInfo.Next"] = 8, ["Xr.ActionSpaceCreateInfo.Action"] = 16, ["Xr.ActionSpaceCreateInfo.SubactionPath"] = 24,
        ["Xr.ActionSpaceCreateInfo.PoseInActionSpace"] = 32,
    };

    // RA01: every struct the mod hands across (nested in Xr, Win32 and ControllerMapping). Cost - CPU: extremely low; GPU: none.
    static IEnumerable<Type> Structs() =>
        new[] { typeof(Xr), typeof(Win32), typeof(ControllerMapping) }.SelectMany(t => t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            .Where(t => t.IsValueType && !t.IsEnum);

    // RA01: each struct is exactly the size the official header says, and a new struct gets its official size added here. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Struct_sizes_match_the_official_headers()
    {
        foreach (Type t in Structs())
        {
            string key = t.DeclaringType!.Name + "." + t.Name;
            Assert.True(Size.TryGetValue(key, out int expected), $"{key} is new: add its size from the official header to Tests\\LayoutTests.cs (Size)");
            int actual = (int)typeof(Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(t).Invoke(null, null)!;
            Assert.True(expected == actual, $"{key} is {actual} bytes, the official header says {expected} - a field was added, removed, or has the wrong type");
        }
    }

    // RA01: each OpenXR field sits exactly where openxr.h puts it. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void OpenXR_field_places_match_openxr_h()
    {
        foreach (Type t in Structs().Where(t => t.DeclaringType == typeof(Xr)))
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string key = $"Xr.{t.Name}.{f.Name}";
                Assert.True(Offset.TryGetValue(key, out int expected), $"{key} is a new field: add its place from openxr.h to Tests\\LayoutTests.cs (Offset)");
                int actual = (int)Marshal.OffsetOf(t, f.Name);
                Assert.True(expected == actual, $"{key} sits at byte {actual}, openxr.h puts it at {expected}");
            }
    }
}
