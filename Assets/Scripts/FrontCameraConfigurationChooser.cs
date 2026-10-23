using Unity.Collections;
using UnityEngine.XR.ARSubsystems;

namespace GuitarAR
{
    /// <summary>
    /// When the front camera is requested, only considers ARKit configurations that run on the
    /// front camera. The default chooser can pick back-camera world tracking instead, because
    /// that configuration can also track a face with the front camera and so scores as well.
    /// Otherwise behaves like the default chooser.
    /// </summary>
    public class FrontCameraConfigurationChooser : ConfigurationChooser
    {
        static readonly DefaultConfigurationChooser s_Default = new DefaultConfigurationChooser();

        public override Configuration ChooseConfiguration(NativeSlice<ConfigurationDescriptor> descriptors, Feature requestedFeatures)
        {
            if ((requestedFeatures & Feature.UserFacingCamera) == 0)
                return s_Default.ChooseConfiguration(descriptors, requestedFeatures);

            int bestCount = -1;
            int bestRank = int.MinValue;
            ConfigurationDescriptor? best = null;
            foreach (var descriptor in descriptors)
            {
                var caps = descriptor.capabilities;
                if ((caps & Feature.UserFacingCamera) == 0 || (caps & Feature.WorldFacingCamera) != 0)
                    continue;

                int count = requestedFeatures.Intersection(caps).Count();
                if (count > bestCount || (count == bestCount && descriptor.rank > bestRank))
                {
                    bestCount = count;
                    bestRank = descriptor.rank;
                    best = descriptor;
                }
            }

            if (best == null)
                return s_Default.ChooseConfiguration(descriptors, requestedFeatures);

            return new Configuration(best.Value, requestedFeatures.Intersection(best.Value.capabilities));
        }
    }
}
