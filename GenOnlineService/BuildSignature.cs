using System.Security.Cryptography;
using System.Text;

namespace GenOnlineService
{
	public enum EBuildSignature
	{
		None,
		Release,
		Dev
	}

	// A build is let in without a crcfiles slot when its exe CRC is signed by a
	// trusted key. The client sends the contents of <exe>.buildsig, written by
	// scripts/zulugo/sign-build.sh: "<release|dev> <base64 DER ECDSA P-256 sig>"
	// over "zulugo-build:<variant>:<execrc>". Only the public keys live here:
	// signkeys/prod/*.pem may sign release and dev builds, signkeys/dev/*.pem
	// only dev builds. Delete a .pem to revoke it.
	public static class BuildSignature
	{
		public static EBuildSignature Check(string? buildsig, UInt32 execrc)
		{
			string root = Path.Combine(Directory.GetCurrentDirectory(), "signkeys");
			return Check(buildsig, execrc, ReadKeys(Path.Combine(root, "prod")), ReadKeys(Path.Combine(root, "dev")));
		}

		public static EBuildSignature Check(string? buildsig, UInt32 execrc, IEnumerable<string> prodKeys, IEnumerable<string> devKeys)
		{
			if (string.IsNullOrWhiteSpace(buildsig))
			{
				return EBuildSignature.None;
			}

			string[] parts = buildsig.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2 || (parts[0] != "release" && parts[0] != "dev"))
			{
				return EBuildSignature.None;
			}

			byte[] sig;
			try
			{
				sig = Convert.FromBase64String(parts[1]);
			}
			catch (FormatException)
			{
				return EBuildSignature.None;
			}

			bool bRelease = parts[0] == "release";
			byte[] message = Encoding.ASCII.GetBytes($"zulugo-build:{parts[0]}:{execrc}");
			IEnumerable<string> keys = bRelease ? prodKeys : prodKeys.Concat(devKeys);
			foreach (string pem in keys)
			{
				if (Verify(pem, message, sig))
				{
					return bRelease ? EBuildSignature.Release : EBuildSignature.Dev;
				}
			}
			return EBuildSignature.None;
		}

		private static bool Verify(string pem, byte[] message, byte[] sig)
		{
			try
			{
				using ECDsa key = ECDsa.Create();
				key.ImportFromPem(pem);
				return key.VerifyData(message, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
			}
			catch (Exception)
			{
				// a malformed key file must not take the version check down with it
				return false;
			}
		}

		private static IEnumerable<string> ReadKeys(string dir)
		{
			if (!Directory.Exists(dir))
			{
				return Array.Empty<string>();
			}
			return Directory.GetFiles(dir, "*.pem").Select(File.ReadAllText).ToArray();
		}
	}
}
