using System.Security.Cryptography;
using System.Text;
using GenOnlineService;
using Xunit;

namespace GenOnlineService.Tests
{
	public class BuildSignatureTests
	{
		private static string Sign(ECDsa key, string variant, UInt32 crc)
		{
			byte[] sig = key.SignData(Encoding.ASCII.GetBytes($"zulugo-build:{variant}:{crc}"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
			return $"{variant} {Convert.ToBase64String(sig)}\n";
		}

		[Fact]
		public void Prod_key_signs_release_and_dev_but_a_dev_key_only_signs_dev()
		{
			using ECDsa prod = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			using ECDsa dev = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			string[] prodKeys = { prod.ExportSubjectPublicKeyInfoPem() };
			string[] devKeys = { dev.ExportSubjectPublicKeyInfoPem() };

			Assert.Equal(EBuildSignature.Release, BuildSignature.Check(Sign(prod, "release", 42), 42, prodKeys, devKeys));
			Assert.Equal(EBuildSignature.Dev, BuildSignature.Check(Sign(prod, "dev", 42), 42, prodKeys, devKeys));
			Assert.Equal(EBuildSignature.Dev, BuildSignature.Check(Sign(dev, "dev", 42), 42, prodKeys, devKeys));
			Assert.Equal(EBuildSignature.None, BuildSignature.Check(Sign(dev, "release", 42), 42, prodKeys, devKeys));
		}

		[Fact]
		public void A_signature_only_covers_the_exe_it_was_made_for()
		{
			using ECDsa dev = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			string[] devKeys = { dev.ExportSubjectPublicKeyInfoPem() };
			string sig = Sign(dev, "dev", 42);

			Assert.Equal(EBuildSignature.None, BuildSignature.Check(sig, 43, Array.Empty<string>(), devKeys));
			Assert.Equal(EBuildSignature.None, BuildSignature.Check(sig.Replace("dev ", "release "), 42, devKeys, devKeys));
			Assert.Equal(EBuildSignature.None, BuildSignature.Check("dev not-base64!", 42, Array.Empty<string>(), devKeys));
			Assert.Equal(EBuildSignature.None, BuildSignature.Check(sig, 42, Array.Empty<string>(), new[] { "garbage" }));
			Assert.Equal(EBuildSignature.None, BuildSignature.Check(null, 42, Array.Empty<string>(), devKeys));
		}
	}
}
