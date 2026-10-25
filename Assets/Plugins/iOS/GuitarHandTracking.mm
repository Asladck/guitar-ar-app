#import <ARKit/ARKit.h>
#import <Vision/Vision.h>
#import <UIKit/UIKit.h>

// Finds hands in the AR camera image with Apple Vision (ARKit has no hand tracking on iPhone).
// Unity calls GuitarHands_Process every frame; it starts a detection on the newest camera frame
// unless one is still running. GuitarHands_Get copies out the latest result: for each hand, the
// joints below as (x, y, confidence), with x and y in normalized screen coordinates
// (0..1, origin top left), already matched to how the camera image is shown on screen.

// AR Foundation's ARKit session native pointer points at this.
typedef struct
{
    int version;
    void* session;
} GuitarARNativeSession;

static const int kMaxHands = 2;
static const int kJointCount = 11;

static float s_Points[kMaxHands * kJointCount * 3];
static int s_Chirality[kMaxHands];
static int s_HandCount;
static double s_Timestamp;
static double s_LastFrameTimestamp;
static volatile BOOL s_Busy;
static dispatch_queue_t s_Queue;
static VNDetectHumanHandPoseRequest* s_Request;
static NSObject* s_Lock;

static NSArray<VNHumanHandPoseObservationJointName>* Joints()
{
    // Order must match GuitarHandPlayer.Joint.
    return @[
        VNHumanHandPoseObservationJointNameWrist,
        VNHumanHandPoseObservationJointNameThumbTip,
        VNHumanHandPoseObservationJointNameIndexMCP,
        VNHumanHandPoseObservationJointNameIndexPIP,
        VNHumanHandPoseObservationJointNameIndexTip,
        VNHumanHandPoseObservationJointNameMiddlePIP,
        VNHumanHandPoseObservationJointNameMiddleTip,
        VNHumanHandPoseObservationJointNameRingPIP,
        VNHumanHandPoseObservationJointNameRingTip,
        VNHumanHandPoseObservationJointNameLittlePIP,
        VNHumanHandPoseObservationJointNameLittleTip,
    ];
}

extern "C" void GuitarHands_Process(void* nativeSession, float viewWidth, float viewHeight, int interfaceOrientation)
{
    if (nativeSession == NULL || s_Busy)
        return;

    GuitarARNativeSession* native = (GuitarARNativeSession*)nativeSession;
    if (native->session == NULL)
        return;

    ARSession* session = (__bridge ARSession*)native->session;
    ARFrame* frame = session.currentFrame;
    if (frame == nil || frame.timestamp == s_LastFrameTimestamp)
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
    CGAffineTransform toView = [frame displayTransformForOrientation:(UIInterfaceOrientation)interfaceOrientation
                                                        viewportSize:CGSizeMake(viewWidth, viewHeight)];
    double timestamp = frame.timestamp;
    s_Busy = YES;

    dispatch_async(s_Queue, ^{
        @autoreleasepool
        {
            VNImageRequestHandler* handler = [[VNImageRequestHandler alloc] initWithCVPixelBuffer:image
                                                                                     orientation:kCGImagePropertyOrientationUp
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
                        CGPoint view = CGPointApplyAffineTransform(CGPointMake(p.location.x, 1.0 - p.location.y), toView);
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

// Copies the latest hands into points (room for maxHands * 11 * 3 floats) and chirality (-1 left,
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
