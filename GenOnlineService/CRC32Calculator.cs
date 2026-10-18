/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace GenOnlineService
{
	public static class CRC32Calculator
	{
		// Version checks hash the same exes on every request, so cache per path and recompute only
		// when the file's size or last write time changes (e.g. a new exe is dropped into crcfiles).
		private static readonly ConcurrentDictionary<string, (long length, DateTime lastWriteUtc, uint crc)> s_dictCache = new();

		public static uint CalculateCRC32(string filePath)
		{
			FileInfo fileInfo = new FileInfo(filePath);
			if (!fileInfo.Exists)
				throw new FileNotFoundException($"File not found: {filePath}");

			if (s_dictCache.TryGetValue(filePath, out var cached)
				&& cached.length == fileInfo.Length
				&& cached.lastWriteUtc == fileInfo.LastWriteTimeUtc)
			{
				return cached.crc;
			}

			uint crc;
			using (var stream = File.OpenRead(filePath))
			{
				var crc32 = new Crc32();
				crc = crc32.ComputeHashFromStream(stream);
			}

			s_dictCache[filePath] = (fileInfo.Length, fileInfo.LastWriteTimeUtc, crc);
			return crc;
		}
	}

	public class Crc32 : HashAlgorithm
	{
		private const uint Polynomial = 0xedb88320;
		private readonly uint[] _table = new uint[256];
		private uint _hash;

		public Crc32()
		{
			for (uint i = 0; i < 256; i++)
			{
				uint crc = i;
				for (int j = 8; j > 0; j--)
				{
					if ((crc & 1) == 1)
						crc = (crc >> 1) ^ Polynomial;
					else
						crc >>= 1;
				}
				_table[i] = crc;
			}
			Initialize();
		}

		public override void Initialize()
		{
			_hash = 0xffffffff;
		}

		protected override void HashCore(byte[] array, int ibStart, int cbSize)
		{
			for (int i = ibStart; i < ibStart + cbSize; i++)
			{
				byte index = (byte)((_hash & 0xff) ^ array[i]);
				_hash = (_hash >> 8) ^ _table[index];
			}
		}

		protected override byte[] HashFinal()
		{
			_hash ^= 0xffffffff;
			return BitConverter.GetBytes(_hash);
		}

		public override int HashSize => 32;

		public uint ComputeHashFromStream(Stream inputStream)
		{
			var buffer = new byte[4096];
			int bytesRead;
			while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
			{
				HashCore(buffer, 0, bytesRead);
			}
			return BitConverter.ToUInt32(HashFinal(), 0);
		}
	}
}
