#import <AVFoundation/AVFoundation.h>

// Unity's default audio session is muted by the iPhone's silent mode. Use the playback
// category music apps use, mixing with other audio instead of stopping it.
// Returns 1 if the category changed, so Unity's audio output must be restarted.
extern "C" int GuitarAR_PlayEvenWhenSilent()
{
    AVAudioSession* session = [AVAudioSession sharedInstance];
    if ([session.category isEqualToString:AVAudioSessionCategoryPlayback])
        return 0;

    NSError* error = nil;
    if (![session setCategory:AVAudioSessionCategoryPlayback withOptions:AVAudioSessionCategoryOptionMixWithOthers error:&error])
    {
        NSLog(@"[GuitarAR] setCategory failed: %@", error);
        return 0;
    }
    [session setActive:YES error:nil];
    return 1;
}

// One line about the audio session for the on-screen diagnostics. Unity frees the string.
extern "C" char* GuitarAR_AudioInfo()
{
    AVAudioSession* session = [AVAudioSession sharedInstance];
    NSString* outputs = @"";
    for (AVAudioSessionPortDescription* port in session.currentRoute.outputs)
        outputs = [outputs stringByAppendingFormat:@"%@ ", port.portType];
    NSString* info = [NSString stringWithFormat:@"%@, громкость %.2f, %.0f Гц, выход: %@",
        [session.category stringByReplacingOccurrencesOfString:@"AVAudioSessionCategory" withString:@""],
        session.outputVolume, session.sampleRate, outputs];
    return strdup(info.UTF8String);
}
