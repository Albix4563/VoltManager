using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SetupPublicContractTests
    {
        [Fact]
        public void Existing_setup_entry_points_keep_their_public_signatures()
        {
            Assert.NotNull(typeof(InstallEngine).GetConstructor(Type.EmptyTypes));
            Assert.NotNull(typeof(UpdateInstallCoordinator).GetConstructor(new[] { typeof(InstallEngine) }));

            MethodInfo install = Assert.Single(typeof(InstallEngine).GetMethods(), method =>
                method.Name == nameof(InstallEngine.InstallAsync) && method.IsPublic);
            Assert.Equal(typeof(Task), install.ReturnType);
            Assert.Equal(
                new[] { typeof(InstallOptions), typeof(string), typeof(CancellationToken) },
                Array.ConvertAll(install.GetParameters(), parameter => parameter.ParameterType));

            MethodInfo update = Assert.Single(typeof(InstallEngine).GetMethods(), method =>
                method.Name == nameof(InstallEngine.UpdateAsync) && method.IsPublic);
            Assert.Equal(typeof(Task), update.ReturnType);
            Assert.Equal(
                new[] { typeof(int), typeof(string), typeof(CancellationToken) },
                Array.ConvertAll(update.GetParameters(), parameter => parameter.ParameterType));

            MethodInfo uninstall = Assert.Single(typeof(HardenedInstallEngine).GetMethods(), method =>
                method.Name == nameof(HardenedInstallEngine.UninstallAsync) && method.DeclaringType == typeof(HardenedInstallEngine));
            Assert.Equal(typeof(Task<UninstallResult>), uninstall.ReturnType);
            Assert.Equal(
                new[] { typeof(string), typeof(CancellationToken) },
                Array.ConvertAll(uninstall.GetParameters(), parameter => parameter.ParameterType));
        }
    }
}
