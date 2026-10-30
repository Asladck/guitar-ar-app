using UnityEditor;
using UnityEngine;

namespace GuitarAR.Editor
{
    /// <summary>
    /// Imports the guitar recordings uncompressed: there are few and they're short, so they load
    /// at once and keep all their sound, and GuitarSamples can read them to make muted notes.
    /// </summary>
    class GuitarSampleImporter : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            if (!assetPath.Replace('\\', '/').Contains("/Resources/GuitarSamples/"))
                return;

            var importer = (AudioImporter)assetImporter;
            var settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.PCM;
            importer.defaultSampleSettings = settings;
            importer.forceToMono = true;
        }
    }
}
