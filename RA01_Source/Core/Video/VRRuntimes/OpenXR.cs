using System.Runtime.InteropServices;

// RA01: many struct fields are filled in by the OpenXR runtime, never by us, so the compiler's "never assigned" warning does not apply.
#pragma warning disable CS0649

namespace RA01.Core;

// RA01: the small part of OpenXR this mod needs, written by hand from openxr.h so no OpenXR wrapper library is needed.
// every constant carries its official name and every struct copies the field order of the header, so both can be checked against it.
// structs are blittable (plain numbers and pointers), so they are passed to the runtime exactly as C would.
// Cost - CPU: extremely low (constants, plain structs and function pointers with no work of their own; the cost is that of the runtime calls made through them); GPU: none (nothing is recorded here).
internal static unsafe class Xr
{
    // RA01: structure type tags. Every OpenXR struct starts with one so the runtime knows what it was handed.
    public const int TypeInstanceCreateInfo = 3;                  // RA01: XR_TYPE_INSTANCE_CREATE_INFO
    public const int TypeSystemGetInfo = 4;                       // RA01: XR_TYPE_SYSTEM_GET_INFO
    public const int TypeSystemProperties = 5;                    // RA01: XR_TYPE_SYSTEM_PROPERTIES
    public const int TypeViewLocateInfo = 6;                      // RA01: XR_TYPE_VIEW_LOCATE_INFO
    public const int TypeView = 7;                                // RA01: XR_TYPE_VIEW
    public const int TypeSessionCreateInfo = 8;                   // RA01: XR_TYPE_SESSION_CREATE_INFO
    public const int TypeSwapchainCreateInfo = 9;                 // RA01: XR_TYPE_SWAPCHAIN_CREATE_INFO
    public const int TypeSessionBeginInfo = 10;                   // RA01: XR_TYPE_SESSION_BEGIN_INFO
    public const int TypeViewState = 11;                          // RA01: XR_TYPE_VIEW_STATE
    public const int TypeFrameEndInfo = 12;                       // RA01: XR_TYPE_FRAME_END_INFO
    public const int TypeEventDataBuffer = 16;                    // RA01: XR_TYPE_EVENT_DATA_BUFFER
    public const int TypeEventDataInstanceLossPending = 17;       // RA01: XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING
    public const int TypeEventDataSessionStateChanged = 18;       // RA01: XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED
    public const int TypeInstanceProperties = 32;                 // RA01: XR_TYPE_INSTANCE_PROPERTIES
    public const int TypeFrameWaitInfo = 33;                      // RA01: XR_TYPE_FRAME_WAIT_INFO
    public const int TypeCompositionLayerProjection = 35;         // RA01: M5b-1 - XR_TYPE_COMPOSITION_LAYER_PROJECTION
    public const int TypeCompositionLayerQuad = 36;               // RA01: XR_TYPE_COMPOSITION_LAYER_QUAD
    public const int TypeCompositionLayerProjectionView = 48;     // RA01: M5b-1 - XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW
    public const int TypeReferenceSpaceCreateInfo = 37;           // RA01: XR_TYPE_REFERENCE_SPACE_CREATE_INFO
    public const int TypeEventDataReferenceSpaceChangePending = 40; // RA01: XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING
    public const int TypeViewConfigurationView = 41;              // RA01: XR_TYPE_VIEW_CONFIGURATION_VIEW
    public const int TypeSpaceLocation = 42;                      // RA01: XR_TYPE_SPACE_LOCATION
    public const int TypeFrameState = 44;                         // RA01: XR_TYPE_FRAME_STATE
    public const int TypeFrameBeginInfo = 46;                     // RA01: XR_TYPE_FRAME_BEGIN_INFO
    public const int TypeSwapchainImageAcquireInfo = 55;          // RA01: XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO
    public const int TypeSwapchainImageWaitInfo = 56;             // RA01: XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO
    public const int TypeSwapchainImageReleaseInfo = 57;          // RA01: XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO
    // RA01: M6-0 - the action-set (controller input) structure types, checked against the Khronos openxr.h on GitHub before use.
    public const int TypeActionStateBoolean = 23;                 // RA01: XR_TYPE_ACTION_STATE_BOOLEAN
    public const int TypeActionStateFloat = 24;                   // RA01: XR_TYPE_ACTION_STATE_FLOAT
    public const int TypeActionStateVector2f = 25;                // RA01: XR_TYPE_ACTION_STATE_VECTOR2F
    public const int TypeActionStatePose = 27;                    // RA01: XR_TYPE_ACTION_STATE_POSE
    public const int TypeActionSetCreateInfo = 28;                // RA01: XR_TYPE_ACTION_SET_CREATE_INFO
    public const int TypeActionCreateInfo = 29;                   // RA01: XR_TYPE_ACTION_CREATE_INFO
    public const int TypeActionSpaceCreateInfo = 38;              // RA01: XR_TYPE_ACTION_SPACE_CREATE_INFO
    public const int TypeInteractionProfileSuggestedBinding = 51; // RA01: XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING
    public const int TypeActionStateGetInfo = 58;                 // RA01: XR_TYPE_ACTION_STATE_GET_INFO
    public const int TypeSessionActionSetsAttachInfo = 60;        // RA01: XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO
    public const int TypeActionsSyncInfo = 61;                    // RA01: XR_TYPE_ACTIONS_SYNC_INFO

    // RA01: M6-0 - what kind of value an action reports. XR_ACTION_TYPE_... (vibration output is not used by this mod).
    public const int ActionTypeBoolean = 1;                       // RA01: XR_ACTION_TYPE_BOOLEAN_INPUT
    public const int ActionTypeFloat = 2;                         // RA01: XR_ACTION_TYPE_FLOAT_INPUT
    public const int ActionTypeVector2f = 3;                      // RA01: XR_ACTION_TYPE_VECTOR2F_INPUT
    public const int ActionTypePose = 4;                          // RA01: XR_ACTION_TYPE_POSE_INPUT
    public const int TypeGraphicsBindingD3D12 = 1000028000;       // RA01: XR_TYPE_GRAPHICS_BINDING_D3D12_KHR
    public const int TypeSwapchainImageD3D12 = 1000028001;        // RA01: XR_TYPE_SWAPCHAIN_IMAGE_D3D12_KHR
    public const int TypeGraphicsRequirementsD3D12 = 1000028002;  // RA01: XR_TYPE_GRAPHICS_REQUIREMENTS_D3D12_KHR

    // RA01: the choices this mod makes.
    public const int FormFactorHeadMountedDisplay = 1;            // RA01: XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY
    public const int ViewConfigurationPrimaryStereo = 2;          // RA01: XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO
    public const int ReferenceSpaceView = 1;                      // RA01: XR_REFERENCE_SPACE_TYPE_VIEW
    public const int ReferenceSpaceLocal = 2;                     // RA01: XR_REFERENCE_SPACE_TYPE_LOCAL
    public const int ReferenceSpaceStage = 3;                     // RA01: XR_REFERENCE_SPACE_TYPE_STAGE (origin on the floor)
    public static readonly string[] ReferenceSpaceNames = { "unknown", "view", "local", "stage" };
    public const int EyeVisibilityBoth = 0;                       // RA01: XR_EYE_VISIBILITY_BOTH (left is 1 and right is 2; the mod's eye numbers are the same, see HeadPose.Publish)
    public const ulong SwapchainUsageColorAttachment = 0x1;       // RA01: XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT
    public const ulong SwapchainUsageSampled = 0x20;              // RA01: XR_SWAPCHAIN_USAGE_SAMPLED_BIT
    public const ulong LayerBlendTextureSourceAlpha = 2;          // RA01: XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT: the layer's own alpha says what is see-through (M5a-3, the F10 window)
    public const ulong LayerUnpremultipliedAlpha = 4;             // RA01: M10-2 - XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT: the colours are NOT already multiplied by alpha, so the compositor does it (a colour with zero alpha then shows nothing)
    public const int BlendModeOpaque = 1;                         // RA01: XR_ENVIRONMENT_BLEND_MODE_OPAQUE

    // RA01: the runtime tells us where the session is in its life through these states.
    public const int StateIdle = 1;                               // RA01: XR_SESSION_STATE_IDLE
    public const int StateReady = 2;                              // RA01: XR_SESSION_STATE_READY
    public const int StateSynchronized = 3;                       // RA01: XR_SESSION_STATE_SYNCHRONIZED
    public const int StateVisible = 4;                            // RA01: XR_SESSION_STATE_VISIBLE
    public const int StateFocused = 5;                            // RA01: XR_SESSION_STATE_FOCUSED
    public const int StateStopping = 6;                           // RA01: XR_SESSION_STATE_STOPPING
    public const int StateLossPending = 7;                        // RA01: XR_SESSION_STATE_LOSS_PENDING
    public const int StateExiting = 8;                            // RA01: XR_SESSION_STATE_EXITING
    public static readonly string[] StateNames = { "unknown", "idle", "ready", "synchronized", "visible", "focused", "stopping", "loss pending", "exiting" };

    public const int TimeoutExpired = 1;                          // RA01: XR_TIMEOUT_EXPIRED
    public const int EventUnavailable = 4;                        // RA01: XR_EVENT_UNAVAILABLE
    public const ulong ViewOrientationValid = 1;                  // RA01: XR_VIEW_STATE_ORIENTATION_VALID_BIT
    public const ulong ViewPositionValid = 2;                     // RA01: XR_VIEW_STATE_POSITION_VALID_BIT
    public const ulong SpaceLocationOrientationValid = 0x1;       // RA01: XR_SPACE_LOCATION_ORIENTATION_VALID_BIT
    public const ulong SpaceLocationPositionValid = 0x2;          // RA01: XR_SPACE_LOCATION_POSITION_VALID_BIT
    public const ulong ViewStatePositionValid = 0x2;              // RA01: XR_VIEW_STATE_POSITION_VALID_BIT
    public const string ExtensionD3D12 = "XR_KHR_D3D12_enable";   // RA01: XR_KHR_D3D12_ENABLE_EXTENSION_NAME
    public const string ExtensionHpController = "XR_EXT_hp_mixed_reality_controller"; // RA01: M50 - XR_EXT_HP_MIXED_REALITY_CONTROLLER_EXTENSION_NAME (HP Reverb G2 controllers)
    public const int TypeExtensionProperties = 2;                 // RA01: M50 - XR_TYPE_EXTENSION_PROPERTIES
    public const string ExtensionFrameController = "XR_VALVE_frame_controller_interaction"; // RA01: M50-2 - Steam Frame controllers (Valve's Steam Frame input docs)
    public const int TypeInteractionProfileState = 53;            // RA01: M50-2 - XR_TYPE_INTERACTION_PROFILE_STATE

    // RA01: OpenXR packs a version as major (16 bits), minor (16 bits) and patch (32 bits) in one 64-bit number.
    // Cost - CPU: extremely low (two shifts and two ors, run once at start-up); GPU: none (pure maths).
    public static ulong MakeVersion(ulong major, ulong minor, ulong patch) => (major << 48) | (minor << 32) | patch;

    // RA01: the plain maths and size structs of OpenXR (rotation, position, pose, field of view, rectangles), copied by value in every pose and view the mod handles.
    // Cost - CPU: extremely low (plain data with no code, a copy of a few dozen bytes, used per headset frame); GPU: none (plain data).
    public struct Quaternionf { public float X, Y, Z, W; }
    public struct Vector3f { public float X, Y, Z; }
    public struct Posef { public Quaternionf Orientation; public Vector3f Position; }
    public struct Fovf { public float AngleLeft, AngleRight, AngleUp, AngleDown; }
    public struct Offset2Di { public int X, Y; }
    public struct Extent2Di { public int Width, Height; }
    public struct Rect2Di { public Offset2Di Offset; public Extent2Di Extent; }
    public struct Extent2Df { public float Width, Height; }
    public struct Vector2f { public float X, Y; }  // RA01: M6-0 - a thumbstick's position, -1 to 1 on each axis

    // RA01: the structs filled in once at start-up to create the OpenXR instance and ask it about the runtime and the headset (application info, instance create info, instance properties, system get info).
    // Cost - CPU: extremely low (plain data with no code, built on the stack once at start-up; the cost is that of the OpenXR runtime call they are passed to); GPU: none (plain data).
    public struct ApplicationInfo
    {
        public fixed byte ApplicationName[128];
        public uint ApplicationVersion;
        public fixed byte EngineName[128];
        public uint EngineVersion;
        public ulong ApiVersion;
    }

    public struct InstanceCreateInfo
    {
        public int Type; public void* Next;
        public ulong CreateFlags;
        public ApplicationInfo ApplicationInfo;
        public uint EnabledApiLayerCount; public byte** EnabledApiLayerNames;
        public uint EnabledExtensionCount; public byte** EnabledExtensionNames;
    }

    public struct InstanceProperties
    {
        public int Type; public void* Next;
        public ulong RuntimeVersion;
        public fixed byte RuntimeName[128];
    }

    // RA01: M50 - one extension the runtime offers (xrEnumerateInstanceExtensionProperties fills a list of these): its name and version.
    // Cost - CPU: extremely low (plain data, a list of a few dozen filled once at start-up); GPU: none (plain data).
    public struct ExtensionProperties
    {
        public int Type; public void* Next;
        public fixed byte ExtensionName[128];
        public uint ExtensionVersion;
    }

    // RA01: M50-2 - which controller layout the runtime is using for one hand right now (xrGetCurrentInteractionProfile); 0 = none yet.
    // Cost - CPU: extremely low (a 24-byte struct, filled once a second); GPU: none (plain data).
    public struct InteractionProfileState
    {
        public int Type; public void* Next;
        public ulong InteractionProfile;
    }

    public struct SystemGetInfo
    {
        public int Type; public void* Next;
        public int FormFactor;
    }

    // RA01: the header nests the graphics and tracking properties inside this struct, here they are laid out flat in the same order.
    // Cost - CPU: extremely low (plain data with no code, filled by the runtime once at start-up; the cost is that of xrGetSystemProperties); GPU: none (plain data).
    public struct SystemProperties
    {
        public int Type; public void* Next;
        public ulong SystemId;
        public uint VendorId;
        public fixed byte SystemName[256];
        public uint MaxSwapchainImageHeight, MaxSwapchainImageWidth, MaxLayerCount;
        public uint OrientationTracking, PositionTracking;
    }

    // RA01: the request to make the OpenXR session, naming the headset system to use.
    // Cost - CPU: extremely low (plain data with no code, built once at start-up; the cost is that of xrCreateSession); GPU: none (plain data).
    public struct SessionCreateInfo
    {
        public int Type; public void* Next;
        public ulong CreateFlags;
        public ulong SystemId;
    }

    // RA01: chained onto SessionCreateInfo.Next to tell the runtime which DX12 device and queue to work with.
    // Cost - CPU: extremely low (plain data with no code, built once at start-up); GPU: none (plain data, it only names the device and queue the runtime will use).
    public struct GraphicsBindingD3D12
    {
        public int Type; public void* Next;
        public nint Device;
        public nint Queue;
    }

    // RA01: the header's adapterLuid (a LUID) is written flat as its two halves.
    // Cost - CPU: extremely low (plain data with no code, filled by the runtime once at start-up); GPU: none (plain data).
    public struct GraphicsRequirementsD3D12
    {
        public int Type; public void* Next;
        public uint AdapterLuidLowPart; public int AdapterLuidHighPart;
        public int MinFeatureLevel;
    }

    // RA01: the request to make a reference space (view, local or stage) with a pose offset.
    // Cost - CPU: extremely low (plain data with no code, built at start-up and whenever a space is made again; the cost is that of xrCreateReferenceSpace); GPU: none (plain data).
    public struct ReferenceSpaceCreateInfo
    {
        public int Type; public void* Next;
        public int ReferenceSpaceType;
        public Posef PoseInReferenceSpace;
    }

    // RA01: the runtime writes any kind of event into this; Type says which struct to read it as.
    // Cost - CPU: extremely low (a 4 KB plain buffer on the stack, cleared and filled by xrPollEvent, a fraction of a microsecond, used a few times per headset frame while events are polled); GPU: none (plain data).
    public struct EventDataBuffer
    {
        public int Type; public void* Next;
        public fixed byte Varying[4000];
    }

    // RA01: the event that says the session moved to a new state (idle, ready, focused and so on).
    // Cost - CPU: extremely low (plain data with no code, read only when the state changes, a few times per run); GPU: none (plain data).
    public struct EventDataSessionStateChanged
    {
        public int Type; public void* Next;
        public ulong Session;
        public int State;
        public long Time;
    }

    // RA01: the runtime sends this shortly before it moves a reference space, for example when the user recentres from the headset.
    // Cost - CPU: extremely low (plain data with no code, read only when the runtime moves a space, rarely); GPU: none (plain data).
    public struct EventDataReferenceSpaceChangePending
    {
        public int Type; public void* Next;
        public ulong Session;
        public int ReferenceSpaceType;
        public long ChangeTime;
        public uint PoseValid;
        public Posef PoseInPreviousSpace;
    }

    // RA01: the structs of the frame loop and view location (begin session, wait frame, begin frame, end frame, view locate info, view state, view), filled and passed to the runtime every headset frame.
    // Cost - CPU: extremely low (plain data with no code, a few hundred bytes on the stack per headset frame; the cost is that of the calls they go to); GPU: none (plain data).
    public struct SessionBeginInfo
    {
        public int Type; public void* Next;
        public int PrimaryViewConfigurationType;
    }

    public struct FrameWaitInfo { public int Type; public void* Next; }

    public struct FrameState
    {
        public int Type; public void* Next;
        public long PredictedDisplayTime, PredictedDisplayPeriod;
        public uint ShouldRender;
    }

    public struct FrameBeginInfo { public int Type; public void* Next; }

    public struct FrameEndInfo
    {
        public int Type; public void* Next;
        public long DisplayTime;
        public int EnvironmentBlendMode;
        public uint LayerCount; public void** Layers;
    }

    public struct ViewLocateInfo
    {
        public int Type; public void* Next;
        public int ViewConfigurationType;
        public long DisplayTime;
        public ulong Space;
    }

    public struct ViewState
    {
        public int Type; public void* Next;
        public ulong ViewStateFlags;
    }

    public struct View
    {
        public int Type; public void* Next;
        public Posef Pose;
        public Fovf Fov;
    }

    // RA01: where a space is, measured in another space. The flags say which parts of the pose can be trusted.
    // Cost - CPU: extremely low (plain data with no code, filled by xrLocateSpace, used per headset frame); GPU: none (plain data).
    public struct SpaceLocation
    {
        public int Type; public void* Next;
        public ulong LocationFlags;
        public Posef Pose;
    }

    // RA01: the request to make a swapchain (the set of images the mod draws into for the headset): size, format and how the images will be used.
    // Cost - CPU: extremely low (plain data with no code, built once per swapchain at start-up; the cost is that of xrCreateSwapchain); GPU: none (plain data, the images are made by the runtime when it is passed on).
    public struct SwapchainCreateInfo
    {
        public int Type; public void* Next;
        public ulong CreateFlags, UsageFlags;
        public long Format;
        public uint SampleCount, Width, Height, FaceCount, ArraySize, MipCount;
    }

    // RA01: one image of a swapchain; Texture is the ID3D12Resource the runtime created for it.
    // Cost - CPU: extremely low (plain data with no code, filled by xrEnumerateSwapchainImages once per swapchain at start-up); GPU: none (plain data, it only holds a pointer to the image).
    public struct SwapchainImageD3D12
    {
        public int Type; public void* Next;
        public nint Texture;
    }

    // RA01: the structs used to take a swapchain image, wait until it is free and give it back, and to say which part of a swapchain a layer shows (acquire, wait and release info, sub image).
    // Cost - CPU: extremely low (plain data with no code, a few dozen bytes on the stack per swapchain image per headset frame; the cost is that of the calls they go to); GPU: none (plain data).
    public struct SwapchainImageAcquireInfo { public int Type; public void* Next; }

    public struct SwapchainImageWaitInfo
    {
        public int Type; public void* Next;
        public long Timeout;
    }

    public struct SwapchainImageReleaseInfo { public int Type; public void* Next; }

    public struct SwapchainSubImage
    {
        public ulong Swapchain;
        public Rect2Di ImageRect;
        public uint ImageArrayIndex;
    }

    // RA01: a flat rectangle floating in a reference space, showing one swapchain image.
    // Cost - CPU: extremely low (plain data with no code, filled once per layer per headset frame and passed to xrEndFrame); GPU: none of its own (plain data; the runtime's compositor draws the quad).
    public struct CompositionLayerQuad
    {
        public int Type; public void* Next;
        public ulong LayerFlags;
        public ulong Space;
        public int EyeVisibility;
        public SwapchainSubImage SubImage;
        public Posef Pose;
        public Extent2Df Size;
    }

    // RA01: M5b-1 - one eye's picture inside a projection layer: the image, the exact spot and direction it was drawn from (Pose) and the exact viewing cone it was
    // drawn through (Fov, four angles in radians: left and down negative). The runtime lays it out itself - no size or distance to guess.
    // Cost - CPU: extremely low (plain data, filled twice per headset frame); GPU: none of its own (the runtime's compositor draws it).
    public struct CompositionLayerProjectionView
    {
        public int Type; public void* Next;
        public Posef Pose;
        public Fovf Fov;
        public SwapchainSubImage SubImage;
    }

    // RA01: M5b-1 - the projection layer: the pictures of both eyes (Views, ViewCount = 2) given as the eye's own view, in one reference space.
    // Cost - CPU: extremely low (plain data, filled once per headset frame); GPU: none of its own (the runtime's compositor draws it - about what two quads cost).
    public struct CompositionLayerProjection
    {
        public int Type; public void* Next;
        public ulong LayerFlags;
        public ulong Space;
        public uint ViewCount;
        public CompositionLayerProjectionView* Views;
    }

    // RA01: the headset's recommended and maximum picture size per eye, as the runtime reports it.
    // Cost - CPU: extremely low (plain data with no code, filled by xrEnumerateViewConfigurationViews once at start-up); GPU: none (plain data).
    public struct ViewConfigurationView
    {
        public int Type; public void* Next;
        public uint RecommendedImageRectWidth, MaxImageRectWidth, RecommendedImageRectHeight, MaxImageRectHeight;
        public uint RecommendedSwapchainSampleCount, MaxSwapchainSampleCount;
    }

    // RA01: M6-0 - the action set (Controllers.cs): one set holding every controller input this mod reads, the actions inside it (one per button/stick/pose), and the per-headset-frame sync and read calls.
    // action names are the fixed-size ASCII buffers used everywhere else in this file (Xr.SetText/Xr.GetText); XrPath and XrAction are both plain 64-bit handles/atoms, same as every other OpenXR handle here.
    // Cost - CPU: extremely low (plain data with no code; built once at start-up (action set, actions, bindings) or once per headset frame (sync info, state get info, the four state structs)); GPU: none (plain data).
    public struct ActionSetCreateInfo
    {
        public int Type; public void* Next;
        public fixed byte ActionSetName[64];
        public fixed byte LocalizedActionSetName[128];
        public uint Priority;
    }

    public struct ActionCreateInfo
    {
        public int Type; public void* Next;
        public fixed byte ActionName[64];
        public int ActionType;
        public uint CountSubactionPaths; public ulong* SubactionPaths;
        public fixed byte LocalizedActionName[128];
    }

    // RA01: one action bound to one input path (e.g. "/user/hand/left/input/trigger/value"). No Type field: unlike the others this one is not its own OpenXR struct header, just a plain pair.
    public struct ActionSuggestedBinding { public ulong Action; public ulong Binding; }

    public struct InteractionProfileSuggestedBinding
    {
        public int Type; public void* Next;
        public ulong InteractionProfile;
        public uint CountSuggestedBindings; public ActionSuggestedBinding* SuggestedBindings;
    }

    public struct SessionActionSetsAttachInfo
    {
        public int Type; public void* Next;
        public uint CountActionSets; public ulong* ActionSets;
    }

    // RA01: which action sets are live this frame, one per set (this mod uses a single set, so always a one-entry array); SubactionPath 0 = every subaction path the set has.
    public struct ActiveActionSet { public ulong ActionSet; public ulong SubactionPath; }

    public struct ActionsSyncInfo
    {
        public int Type; public void* Next;
        public uint CountActiveActionSets; public ActiveActionSet* ActiveActionSets;
    }

    public struct ActionStateGetInfo
    {
        public int Type; public void* Next;
        public ulong Action;
        public ulong SubactionPath;
    }

    public struct ActionStateBoolean
    {
        public int Type; public void* Next;
        public uint CurrentState, ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    public struct ActionStateFloat
    {
        public int Type; public void* Next;
        public float CurrentState;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    public struct ActionStateVector2f
    {
        public int Type; public void* Next;
        public Vector2f CurrentState;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    public struct ActionStatePose
    {
        public int Type; public void* Next;
        public uint IsActive;
    }

    // RA01: makes the XrSpace that tracks a pose action (e.g. a hand's grip), located every headset frame the same way floorSpace/viewSpace already are (Xr.LocateSpace).
    public struct ActionSpaceCreateInfo
    {
        public int Type; public void* Next;
        public ulong Action;
        public ulong SubactionPath;
        public Posef PoseInActionSpace;
    }

    // RA01: the OpenXR functions. Handles (instance, session, space) and the system id are 64-bit numbers, results are 32-bit and negative means failure.
    // RA01: these are plain declarations, so the cost is that of the OpenXR runtime call itself. xrWaitFrame, xrBeginFrame, xrEndFrame, xrLocateViews, xrLocateSpace, xrPollEvent and the swapchain acquire, wait and release
    // RA01: calls run per headset frame (about 90 a second); the rest run at start-up or shut-down. xrWaitFrame sleeps until the headset wants the next frame, which is waiting, not work.
    // RA01: Cost - CPU: low (one runtime call each, normally microseconds, some tens per headset frame); GPU: low (only xrEndFrame and xrReleaseSwapchainImage lead to GPU work, in the runtime's compositor; the rest none).
    public static delegate* unmanaged<ulong, byte*, nint*, int> GetInstanceProcAddr;
    public static delegate* unmanaged<InstanceCreateInfo*, ulong*, int> CreateInstance;
    public static delegate* unmanaged<byte*, uint, uint*, ExtensionProperties*, int> EnumerateInstanceExtensionProperties; // RA01: M50 - may be null (looked up without the must-exist check), see Load
    public static delegate* unmanaged<ulong, ulong, InteractionProfileState*, int> GetCurrentInteractionProfile; // RA01: M50-2 - may be null, see Load
    public static delegate* unmanaged<ulong, int> DestroyInstance;
    public static delegate* unmanaged<ulong, InstanceProperties*, int> GetInstanceProperties;
    public static delegate* unmanaged<ulong, SystemGetInfo*, ulong*, int> GetSystem;
    public static delegate* unmanaged<ulong, ulong, SystemProperties*, int> GetSystemProperties;
    public static delegate* unmanaged<ulong, ulong, int, uint, uint*, ViewConfigurationView*, int> EnumerateViewConfigurationViews;
    public static delegate* unmanaged<ulong, SessionCreateInfo*, ulong*, int> CreateSession;
    public static delegate* unmanaged<ulong, int> DestroySession;
    public static delegate* unmanaged<ulong, uint, uint*, int*, int> EnumerateReferenceSpaces;
    public static delegate* unmanaged<ulong, ReferenceSpaceCreateInfo*, ulong*, int> CreateReferenceSpace;
    public static delegate* unmanaged<ulong, int> DestroySpace;
    public static delegate* unmanaged<ulong, ulong, long, SpaceLocation*, int> LocateSpace;
    public static delegate* unmanaged<ulong, EventDataBuffer*, int> PollEvent;
    public static delegate* unmanaged<ulong, SessionBeginInfo*, int> BeginSession;
    public static delegate* unmanaged<ulong, int> EndSession;
    public static delegate* unmanaged<ulong, FrameWaitInfo*, FrameState*, int> WaitFrame;
    public static delegate* unmanaged<ulong, FrameBeginInfo*, int> BeginFrame;
    public static delegate* unmanaged<ulong, FrameEndInfo*, int> EndFrame;
    public static delegate* unmanaged<ulong, ViewLocateInfo*, ViewState*, uint, uint*, View*, int> LocateViews;
    public static delegate* unmanaged<ulong, int, byte*, int> ResultToString;
    public static delegate* unmanaged<ulong, uint, uint*, long*, int> EnumerateSwapchainFormats;
    public static delegate* unmanaged<ulong, SwapchainCreateInfo*, ulong*, int> CreateSwapchain;
    public static delegate* unmanaged<ulong, int> DestroySwapchain;
    public static delegate* unmanaged<ulong, uint, uint*, SwapchainImageD3D12*, int> EnumerateSwapchainImages;
    public static delegate* unmanaged<ulong, SwapchainImageAcquireInfo*, uint*, int> AcquireSwapchainImage;
    public static delegate* unmanaged<ulong, SwapchainImageWaitInfo*, int> WaitSwapchainImage;
    public static delegate* unmanaged<ulong, SwapchainImageReleaseInfo*, int> ReleaseSwapchainImage;
    public static delegate* unmanaged<ulong, ulong, GraphicsRequirementsD3D12*, int> GetD3D12GraphicsRequirements; // RA01: extension function, see LoadExtensions

    // RA01: M6-0 - the action-set functions (Controllers.cs). StringToPath turns a binding path string ("/user/hand/left/input/trigger/value") into the XrPath atom every other call here uses.
    public static delegate* unmanaged<ulong, byte*, ulong*, int> StringToPath;
    public static delegate* unmanaged<ulong, ActionSetCreateInfo*, ulong*, int> CreateActionSet;
    public static delegate* unmanaged<ulong, int> DestroyActionSet;
    public static delegate* unmanaged<ulong, ActionCreateInfo*, ulong*, int> CreateAction;
    public static delegate* unmanaged<ulong, int> DestroyAction;
    public static delegate* unmanaged<ulong, InteractionProfileSuggestedBinding*, int> SuggestInteractionProfileBindings;
    public static delegate* unmanaged<ulong, SessionActionSetsAttachInfo*, int> AttachSessionActionSets;
    public static delegate* unmanaged<ulong, ActionSpaceCreateInfo*, ulong*, int> CreateActionSpace;
    public static delegate* unmanaged<ulong, ActionsSyncInfo*, int> SyncActions;
    public static delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStateBoolean*, int> GetActionStateBoolean;
    public static delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStateFloat*, int> GetActionStateFloat;
    public static delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStateVector2f*, int> GetActionStateVector2f;
    public static delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStatePose*, int> GetActionStatePose;

    // RA01: the loader DLL exports every core function by name, so they are looked up straight from it.
    // Cost - CPU: extremely low (about thirty GetProcAddress lookups, well under a millisecond in all, run once at start-up); GPU: none (no GPU work).
    public static void Load(nint loader)
    {
        GetInstanceProcAddr = (delegate* unmanaged<ulong, byte*, nint*, int>)Find(loader, "xrGetInstanceProcAddr");
        CreateInstance = (delegate* unmanaged<InstanceCreateInfo*, ulong*, int>)Find(loader, "xrCreateInstance");
        EnumerateInstanceExtensionProperties = (delegate* unmanaged<byte*, uint, uint*, ExtensionProperties*, int>)Win32.GetProcAddress(loader, "xrEnumerateInstanceExtensionProperties"); // RA01: M50 - optional: missing = null, never an error (start-up must not depend on it)
        GetCurrentInteractionProfile = (delegate* unmanaged<ulong, ulong, InteractionProfileState*, int>)Win32.GetProcAddress(loader, "xrGetCurrentInteractionProfile"); // RA01: M50-2 - optional, as above
        DestroyInstance = (delegate* unmanaged<ulong, int>)Find(loader, "xrDestroyInstance");
        GetInstanceProperties = (delegate* unmanaged<ulong, InstanceProperties*, int>)Find(loader, "xrGetInstanceProperties");
        GetSystem = (delegate* unmanaged<ulong, SystemGetInfo*, ulong*, int>)Find(loader, "xrGetSystem");
        GetSystemProperties = (delegate* unmanaged<ulong, ulong, SystemProperties*, int>)Find(loader, "xrGetSystemProperties");
        EnumerateViewConfigurationViews = (delegate* unmanaged<ulong, ulong, int, uint, uint*, ViewConfigurationView*, int>)Find(loader, "xrEnumerateViewConfigurationViews");
        CreateSession = (delegate* unmanaged<ulong, SessionCreateInfo*, ulong*, int>)Find(loader, "xrCreateSession");
        DestroySession = (delegate* unmanaged<ulong, int>)Find(loader, "xrDestroySession");
        EnumerateReferenceSpaces = (delegate* unmanaged<ulong, uint, uint*, int*, int>)Find(loader, "xrEnumerateReferenceSpaces");
        CreateReferenceSpace = (delegate* unmanaged<ulong, ReferenceSpaceCreateInfo*, ulong*, int>)Find(loader, "xrCreateReferenceSpace");
        DestroySpace = (delegate* unmanaged<ulong, int>)Find(loader, "xrDestroySpace");
        LocateSpace = (delegate* unmanaged<ulong, ulong, long, SpaceLocation*, int>)Find(loader, "xrLocateSpace");
        PollEvent = (delegate* unmanaged<ulong, EventDataBuffer*, int>)Find(loader, "xrPollEvent");
        BeginSession = (delegate* unmanaged<ulong, SessionBeginInfo*, int>)Find(loader, "xrBeginSession");
        EndSession = (delegate* unmanaged<ulong, int>)Find(loader, "xrEndSession");
        WaitFrame = (delegate* unmanaged<ulong, FrameWaitInfo*, FrameState*, int>)Find(loader, "xrWaitFrame");
        BeginFrame = (delegate* unmanaged<ulong, FrameBeginInfo*, int>)Find(loader, "xrBeginFrame");
        EndFrame = (delegate* unmanaged<ulong, FrameEndInfo*, int>)Find(loader, "xrEndFrame");
        LocateViews = (delegate* unmanaged<ulong, ViewLocateInfo*, ViewState*, uint, uint*, View*, int>)Find(loader, "xrLocateViews");
        ResultToString = (delegate* unmanaged<ulong, int, byte*, int>)Find(loader, "xrResultToString");
        EnumerateSwapchainFormats = (delegate* unmanaged<ulong, uint, uint*, long*, int>)Find(loader, "xrEnumerateSwapchainFormats");
        CreateSwapchain = (delegate* unmanaged<ulong, SwapchainCreateInfo*, ulong*, int>)Find(loader, "xrCreateSwapchain");
        DestroySwapchain = (delegate* unmanaged<ulong, int>)Find(loader, "xrDestroySwapchain");
        EnumerateSwapchainImages = (delegate* unmanaged<ulong, uint, uint*, SwapchainImageD3D12*, int>)Find(loader, "xrEnumerateSwapchainImages");
        AcquireSwapchainImage = (delegate* unmanaged<ulong, SwapchainImageAcquireInfo*, uint*, int>)Find(loader, "xrAcquireSwapchainImage");
        WaitSwapchainImage = (delegate* unmanaged<ulong, SwapchainImageWaitInfo*, int>)Find(loader, "xrWaitSwapchainImage");
        ReleaseSwapchainImage = (delegate* unmanaged<ulong, SwapchainImageReleaseInfo*, int>)Find(loader, "xrReleaseSwapchainImage");

        // RA01: M6-0 - the action-set functions are core OpenXR 1.0, exported by the loader the same as everything above (no extension needed).
        StringToPath = (delegate* unmanaged<ulong, byte*, ulong*, int>)Find(loader, "xrStringToPath");
        CreateActionSet = (delegate* unmanaged<ulong, ActionSetCreateInfo*, ulong*, int>)Find(loader, "xrCreateActionSet");
        DestroyActionSet = (delegate* unmanaged<ulong, int>)Find(loader, "xrDestroyActionSet");
        CreateAction = (delegate* unmanaged<ulong, ActionCreateInfo*, ulong*, int>)Find(loader, "xrCreateAction");
        DestroyAction = (delegate* unmanaged<ulong, int>)Find(loader, "xrDestroyAction");
        SuggestInteractionProfileBindings = (delegate* unmanaged<ulong, InteractionProfileSuggestedBinding*, int>)Find(loader, "xrSuggestInteractionProfileBindings");
        AttachSessionActionSets = (delegate* unmanaged<ulong, SessionActionSetsAttachInfo*, int>)Find(loader, "xrAttachSessionActionSets");
        CreateActionSpace = (delegate* unmanaged<ulong, ActionSpaceCreateInfo*, ulong*, int>)Find(loader, "xrCreateActionSpace");
        SyncActions = (delegate* unmanaged<ulong, ActionsSyncInfo*, int>)Find(loader, "xrSyncActions");
        GetActionStateBoolean = (delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStateBoolean*, int>)Find(loader, "xrGetActionStateBoolean");
        GetActionStateFloat = (delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStateFloat*, int>)Find(loader, "xrGetActionStateFloat");
        GetActionStateVector2f = (delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStateVector2f*, int>)Find(loader, "xrGetActionStateVector2f");
        GetActionStatePose = (delegate* unmanaged<ulong, ActionStateGetInfo*, ActionStatePose*, int>)Find(loader, "xrGetActionStatePose");
    }

    // RA01: extension functions are not exported, the runtime hands them out per instance through xrGetInstanceProcAddr.
    // Cost - CPU: extremely low (one xrGetInstanceProcAddr call, run once at start-up); GPU: none (no GPU work).
    public static void LoadExtensions(ulong instance)
    {
        nint function = 0;
        fixed (byte* name = "xrGetD3D12GraphicsRequirementsKHR\0"u8)
        {
            int result = GetInstanceProcAddr(instance, name, &function);
            if (result < 0 || function == 0) throw new EntryPointNotFoundException($"xrGetD3D12GraphicsRequirementsKHR not offered by the runtime (result {result})");
        }
        GetD3D12GraphicsRequirements = (delegate* unmanaged<ulong, ulong, GraphicsRequirementsD3D12*, int>)function;
    }

    // RA01: looks one function up by name in the OpenXR loader DLL and stops with an error naming it if it is not there.
    // Cost - CPU: extremely low (one GetProcAddress call, run about thirty times once at start-up from Load); GPU: none (no GPU work).
    static nint Find(nint loader, string name)
    {
        nint function = Win32.GetProcAddress(loader, name);
        if (function == 0) throw new EntryPointNotFoundException($"{name} not found in openxr_loader.dll");
        return function;
    }

    // RA01: OpenXR names are fixed-size ASCII buffers inside the structs.
    // Cost - CPU: extremely low (clears and fills a buffer of at most a few hundred bytes, run twice at start-up); GPU: none (no GPU work).
    public static void SetText(byte* destination, int capacity, string text)
    {
        var buffer = new Span<byte>(destination, capacity);
        buffer.Clear();
        System.Text.Encoding.ASCII.GetBytes(text, buffer[..(capacity - 1)]);
    }

    // RA01: reads a zero-ended UTF-8 name that the runtime wrote (runtime name, headset name, result name) into a normal string, or an empty string if there is none.
    // Cost - CPU: extremely low (one short string copy and allocation, run a few times at start-up and when an error is logged); GPU: none (no GPU work).
    public static string GetText(byte* source) => Marshal.PtrToStringUTF8((nint)source) ?? "";
}
