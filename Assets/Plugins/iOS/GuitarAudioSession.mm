#import <AVFoundation/AVFoundation.h>

// Unity's default audio session is muted by the iPhone's silent mode. Use the playback
// category music apps use, mixing with other audio instead of stopping it.
extern "C" void GuitarAR_PlayEvenWhenSilent()
{
    AVAudioSession* session = [AVAudioSession sharedInstance];
    [session setCategory:AVAudioSessionCategoryPlayback withOptions:AVAudioSessionCategoryOptionMixWithOthers error:nil];
    [session setActive:YES error:nil];
}
