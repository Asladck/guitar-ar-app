#import <ARKit/ARKit.h>
#import <Vision/Vision.h>
#import <UIKit/UIKit.h>

// Finds hands in the AR camera image with Apple Vision (ARKit has no hand tracking on iPhone).
// Unity calls GuitarHands_Process every frame; it starts a detection on the newest camera frame
// unless one is still running. The camera image is stored sideways (the sensor is landscape), so
// Vision is told which way is up as the image is shown, and sees hands upright, as it was trained. GuitarHands_Get copies out the latest result: for each hand, the
// 21 joints below as (x, y, confidence), with x and y in normalized screen coordinates
// (0..1, origin top left), already matched to how the camera image is shown on screen.

// AR Foundation's ARKit session native pointer points at this.
typedef struct
{
    int version;
    void* session;
} GuitarARNativeSession;

static const int kMaxHands = 2;
static const int kJointCount = 21;

static float s_Points[kMaxHands * kJointCount * 3];
static int s_Chirality[kMaxHands];
static int s_HandCount;
static double s_Timestamp;
static double s_LastFrameTimestamp;
static double s_ShownTimestamp;
static volatile BOOL s_Busy;
static dispatch_queue_t s_Queue;
static VNDetectHumanHandPoseRequest* s_Request;
static NSObject* s_Lock;

static NSArray<VNHumanHandPoseObservationJointName>* Joints()
{
    // Order must match GuitarAR.HandJoint (MediaPipe's order).
    return @[
        VNHumanHandPoseObservationJointNameWrist,
        VNHumanHandPoseObservationJointNameThumbCMC,
        VNHumanHandPoseObservationJointNameThumbMP,
        VNHumanHandPoseObservationJointNameThumbIP,
        VNHumanHandPoseObservationJointNameThumbTip,
        VNHumanHandPoseObservationJointNameIndexMCP,
        VNHumanHandPoseObservationJointNameIndexPIP,
        VNHumanHandPoseObservationJointNameIndexDIP,
        VNHumanHandPoseObservationJointNameIndexTip,
        VNHumanHandPoseObservationJointNameMiddleMCP,
        VNHumanHandPoseObservationJointNameMiddlePIP,
        VNHumanHandPoseObservationJointNameMiddleDIP,
        VNHumanHandPoseObservationJointNameMiddleTip,
        VNHumanHandPoseObservationJointNameRingMCP,
        VNHumanHandPoseObservationJointNameRingPIP,
        VNHumanHandPoseObservationJointNameRingDIP,
        VNHumanHandPoseObservationJointNameRingTip,
        VNHumanHandPoseObservationJointNameLittleMCP,
        VNHumanHandPoseObservationJointNameLittlePIP,
        VNHumanHandPoseObservationJointNameLittleDIP,
        VNHumanHandPoseObservationJointNameLittleTip,
    ];
}

// Which way the camera image must be turned to be upright as it's shown: the turn that brings the
// image direction shown as "up" on screen to its top. A mirrored (selfie) view is only flipped
// sideways, so up stays up; Vision is never given a mirrored image, so it still tells left
// hands from right ones as before.
static CGImagePropertyOrientation UprightOrientation(CGAffineTransform toView)
{
    // toView maps normalized image points to the view; solve for the image direction that ends
    // up pointing up the screen (view y grows downwards).
    double det = toView.a * toView.d - toView.b * toView.c;
    if (det == 0)
        return kCGImagePropertyOrientationUp;
    double upX = toView.c / det, upY = -toView.a / det;
    if (fabs(upY) > fabs(upX))
        return upY < 0 ? kCGImagePropertyOrientationUp : kCGImagePropertyOrientationDown;
    // Up is the image's left side: turn it a quarter clockwise (.right), else anticlockwise (.left).
    return upX < 0 ? kCGImagePropertyOrientationRight : kCGImagePropertyOrientationLeft;
}

// How the app is shown now. Asked of iOS itself rather than worked out from Unity's orientation
// names, whose landscape left and right are the other way round from UIKit's.
static UIInterfaceOrientation CurrentInterfaceOrientation(int fallback)
{
    for (UIScene* scene in UIApplication.sharedApplication.connectedScenes)
    {
        if (![scene isKindOfClass:[UIWindowScene class]])
            continue;
        UIInterfaceOrientation orientation = ((UIWindowScene*)scene).interfaceOrientation;
        if (orientation != UIInterfaceOrientationUnknown)
            return orientation;
    }
    return (UIInterfaceOrientation)fallback;
}

// A point Vision found in the turned (upright) image, origin top left, back in the camera image.
static CGPoint ToImage(CGPoint upright, CGImagePropertyOrientation orientation)
{
    switch (orientation)
    {
        case kCGImagePropertyOrientationRight: return CGPointMake(upright.y, 1.0 - upright.x);
        case kCGImagePropertyOrientationLeft: return CGPointMake(1.0 - upright.y, upright.x);
        case kCGImagePropertyOrientationDown: return CGPointMake(1.0 - upright.x, 1.0 - upright.y);
        default: return upright;
    }
}

extern "C" void GuitarHands_Process(void* nativeSession, float viewWidth, float viewHeight, int interfaceOrientation)
{
    if (nativeSession == NULL)
        return;

    GuitarARNativeSession* native = (GuitarARNativeSession*)nativeSession;
    if (native->session == NULL)
        return;

    ARSession* session = (__bridge ARSession*)native->session;
    ARFrame* frame = session.currentFrame;
    if (frame != nil)
        s_ShownTimestamp = frame.timestamp;
    if (s_Busy || frame == nil || frame.timestamp == s_LastFrameTimestamp)
        return;
    s_LastFrameTimestamp = frame.timestamp;

    if (s_Queue == nil)
    {
        s_Queue = dispatch_queue_create("GuitarAR.HandTracking", DISPATCH_QUEUE_SERIAL);
        s_Request = [[VNDetectHumanHandPoseRequest alloc] init];
        s_Request.maximumHandCount = kMaxHands;
        s_Lock = [[NSObject alloc] init];
    }

    CVPixelBufferRef image = frame.capturedImage;
    CVPixelBufferRetain(image);
    CGAffineTransform toView = [frame displayTransformForOrientation:CurrentInterfaceOrientation(interfaceOrientation)
                                                        viewportSize:CGSizeMake(viewWidth, viewHeight)];
    CGImagePropertyOrientation orientation = UprightOrientation(toView);
    double timestamp = frame.timestamp;
    s_Busy = YES;

    dispatch_async(s_Queue, ^{
        @autoreleasepool
        {
            VNImageRequestHandler* handler = [[VNImageRequestHandler alloc] initWithCVPixelBuffer:image
                                                                                     orientation:orientation
                                                                                         options:@{}];
            NSError* error = nil;
            float points[kMaxHands * kJointCount * 3] = {0};
            int chirality[kMaxHands] = {0};
            int handCount = 0;
            if ([handler performRequests:@[s_Request] error:&error])
            {
                NSArray<VNHumanHandPoseObservation*>* hands = s_Request.results;
                NSArray<VNHumanHandPoseObservationJointName>* joints = Joints();
                for (VNHumanHandPoseObservation* hand in hands)
                {
                    if (handCount >= kMaxHands)
                        break;
                    for (int j = 0; j < kJointCount; j++)
                    {
                        VNRecognizedPoint* p = [hand recognizedPointForJointName:joints[j] error:nil];
                        float* out = &points[(handCount * kJointCount + j) * 3];
                        if (p == nil)
                            continue;
                        // Vision's origin is bottom left; ARKit's display transform expects top left.
                        CGPoint image = ToImage(CGPointMake(p.location.x, 1.0 - p.location.y), orientation);
                        CGPoint view = CGPointApplyAffineTransform(image, toView);
                        out[0] = view.x;
                        out[1] = view.y;
                        out[2] = p.confidence;
                    }
                    // -1 left hand, 1 right hand, 0 unknown. Vision judges the hand itself, so a
                    // mirrored selfie preview doesn't change it.
                    if (@available(iOS 15.0, *))
                        chirality[handCount] = (int)hand.chirality;
                    handCount++;
                }
            }
            else
            {
                NSLog(@"[GuitarAR] Hand detection failed: %@", error);
            }
            CVPixelBufferRelease(image);

            @synchronized (s_Lock)
            {
                memcpy(s_Points, points, sizeof(points));
                memcpy(s_Chirality, chirality, sizeof(chirality));
                s_HandCount = handCount;
                s_Timestamp = timestamp;
            }
            s_Busy = NO;
        }
    });
}

// Copies the latest hands into points (room for maxHands * 21 * 3 floats) and chirality (-1 left,
// 1 right, 0 unknown) and returns how many were found. timestamp is the camera frame's time, so
// the caller can tell a new result.
extern "C" int GuitarHands_Get(float* points, int* chirality, int maxHands, double* timestamp)
{
    if (s_Lock == nil)
    {
        *timestamp = 0;
        return 0;
    }

    @synchronized (s_Lock)
    {
        int count = MIN(s_HandCount, maxHands);
        memcpy(points, s_Points, sizeof(float) * count * kJointCount * 3);
        memcpy(chirality, s_Chirality, sizeof(int) * count);
        *timestamp = s_Timestamp;
        return count;
    }
}

// The time of the camera frame on screen now (the newest), on the same clock as GuitarHands_Get's
// timestamp: how far behind the hands found are.
extern "C" double GuitarHands_ShownTime()
{
    return s_ShownTimestamp;
}
