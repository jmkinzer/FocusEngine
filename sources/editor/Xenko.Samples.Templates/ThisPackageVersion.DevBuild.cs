#if !XENKO_PACKAGE_BUILD
namespace Xenko.Samples.Templates
{
    static class ThisPackageVersion
    {
        // we version this package manually because most of the time the samples are big and don't need to be updated
        public static string Current = "4.1.0.1-dev";
    }
}
#endif
