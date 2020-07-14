﻿using AssetsTools.NET.Extra.Decompressors.LZ4;
using SevenZip.Compression.LZMA;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetsTools.NET
{
    public class AssetBundleFile
    {
        public AssetBundleHeader03 bundleHeader3;
        public AssetBundleHeader06 bundleHeader6;

        public AssetsList assetsLists3;
        public AssetBundleBlockAndDirectoryList06 bundleInf6;

        public AssetsFileReader reader;

        ///public AssetsBundleFile();
        ///public ~AssetsBundleFile();
        public void Close()
        {
            reader.Close();
        }
        public bool Read(AssetsFileReader reader, bool allowCompressed = false)
        {
            this.reader = reader;
            reader.ReadNullTerminated();
            uint version = reader.ReadUInt32();
            if (version == 6)
            {
                reader.Position = 0;
                bundleHeader6 = new AssetBundleHeader06();
                bundleHeader6.Read(reader);
                if (bundleHeader6.signature == "UnityFS")
                {
                    bundleInf6 = new AssetBundleBlockAndDirectoryList06();
                    if ((bundleHeader6.flags & 0x3F) != 0)
                    {
                        if (allowCompressed)
                        {
                            return true;
                        }
                        else
                        {
                            Close();
                            return false;
                        }
                    }
                    else
                    {
                        bundleInf6.Read(bundleHeader6.GetBundleInfoOffset(), reader);
                        return true;
                    }
                }
                else
                {
                    new NotImplementedException("Non UnityFS bundles are not supported yet.");
                }
            }
            else if (version == 3)
            {
                new NotImplementedException("Version 3 bundles are not supported yet.");
            }
            else
            {
                new Exception("AssetsBundleFile.Read : Unknown file version!");
            }
            return false;
        }
        public bool Write(AssetsFileWriter writer, List<BundleReplacer> replacers, ClassDatabaseFile typeMeta = null)
        {
            bundleHeader6.Write(writer);

            AssetBundleBlockAndDirectoryList06 newBundleInf6 = new AssetBundleBlockAndDirectoryList06()
            {
                checksumLow = 0,
                checksumHigh = 0
            };
            //I could map the assets to their blocks but I don't
            //have any more-than-1-block files to test on
            //this should work just fine as far as I know
            newBundleInf6.blockInf = new AssetBundleBlockInfo06[]
            {
                new AssetBundleBlockInfo06
                {
                    compressedSize = 0,
                    decompressedSize = 0,
                    flags = 0x40
                }
            };

            //assets that did not have their data modified but need
            //the original info to read from the original file
            var newToOriginalDirInfoLookup = new Dictionary<AssetBundleDirectoryInfo06, AssetBundleDirectoryInfo06>();
            List<AssetBundleDirectoryInfo06> originalDirInfos = new List<AssetBundleDirectoryInfo06>();
            List<AssetBundleDirectoryInfo06> dirInfos = new List<AssetBundleDirectoryInfo06>();
            List<BundleReplacer> currentReplacers = replacers.ToList();
            //this is kind of useless at the moment but leaving it here
            //because if the AssetsFile size can be precalculated in the
            //future, we can use this to skip rewriting sizes
            long currentOffset = 0;

            //write all original files, modify sizes if needed and skip those to be removed
            for (int i = 0; i < bundleInf6.directoryCount; i++)
            {
                AssetBundleDirectoryInfo06 info = bundleInf6.dirInf[i];
                originalDirInfos.Add(info);
                AssetBundleDirectoryInfo06 newInfo = new AssetBundleDirectoryInfo06()
                {
                    offset = currentOffset,
                    decompressedSize = info.decompressedSize,
                    flags = info.flags,
                    name = info.name
                };
                BundleReplacer replacer = currentReplacers.FirstOrDefault(n => n.GetOriginalEntryName() == newInfo.name);
                if (replacer != null)
                {
                    currentReplacers.Remove(replacer);
                    if (replacer.GetReplacementType() == BundleReplacementType.AddOrModify)
                    {
                        newInfo = new AssetBundleDirectoryInfo06()
                        {
                            offset = currentOffset,
                            decompressedSize = replacer.GetSize(),
                            flags = info.flags,
                            name = replacer.GetEntryName()
                        };
                    }
                    else if (replacer.GetReplacementType() == BundleReplacementType.Rename)
                    {
                        newInfo = new AssetBundleDirectoryInfo06()
                        {
                            offset = currentOffset,
                            decompressedSize = info.decompressedSize,
                            flags = info.flags,
                            name = replacer.GetEntryName()
                        };
                        newToOriginalDirInfoLookup[newInfo] = info;
                    }
                    else if (replacer.GetReplacementType() == BundleReplacementType.Remove)
                    {
                        continue;
                    }
                }
                else
                {
                    newToOriginalDirInfoLookup[newInfo] = info;
                }

                if (newInfo.decompressedSize != -1)
                {
                    currentOffset += newInfo.decompressedSize;
                }
            
                dirInfos.Add(newInfo);
            }

            //write new files
            while (currentReplacers.Count > 0)
            {
                BundleReplacer replacer = currentReplacers[0];
                if (replacer.GetReplacementType() == BundleReplacementType.AddOrModify)
                {
                    AssetBundleDirectoryInfo06 info = new AssetBundleDirectoryInfo06()
                    {
                        offset = currentOffset,
                        decompressedSize = replacer.GetSize(),
                        flags = 0x04, //idk it just works (tm)
                        name = replacer.GetEntryName()
                    };
                    currentOffset += info.decompressedSize;

                    dirInfos.Add(info);
                }
                currentReplacers.Remove(replacer);
            }

            //write the listings
            long bundleInfPos = writer.Position;
            newBundleInf6.dirInf = dirInfos.ToArray(); //this is only here to allocate enough space so it's fine if it's inaccurate
            newBundleInf6.Write(writer);

            long assetDataPos = writer.Position;

            //actually write the file data to the bundle now
            for (int i = 0; i < dirInfos.Count; i++)
            {
                AssetBundleDirectoryInfo06 info = dirInfos[i];
                BundleReplacer replacer = replacers.FirstOrDefault(n => n.GetOriginalEntryName() == info.name);
                if (replacer != null)
                {
                    if (replacer.GetReplacementType() == BundleReplacementType.AddOrModify)
                    {
                        long startPos = writer.Position;
                        long endPos = replacer.Write(writer);
                        long size = endPos - startPos;

                        dirInfos[i].decompressedSize = size;
                        dirInfos[i].offset = startPos - assetDataPos;
                    }
                    else if (replacer.GetReplacementType() == BundleReplacementType.Remove)
                    {
                        continue;
                    }
                }
                else
                {
                    if (newToOriginalDirInfoLookup.TryGetValue(info, out AssetBundleDirectoryInfo06 originalInfo))
                    {
                        long startPos = writer.Position;

                        reader.Position = bundleHeader6.GetFileDataOffset() + originalInfo.offset;
                        byte[] assetData = reader.ReadBytes((int)originalInfo.decompressedSize);
                        writer.Write(assetData);

                        dirInfos[i].offset = startPos - assetDataPos;
                    }
                }
            }

            //now that we know what the sizes are of the written files let's go back and fix them
            long finalSize = writer.Position;
            uint assetSize = (uint)(finalSize - assetDataPos);

            writer.Position = bundleInfPos;
            newBundleInf6.blockInf[0].decompressedSize = assetSize;
            newBundleInf6.blockInf[0].compressedSize = assetSize;
            newBundleInf6.dirInf = dirInfos.ToArray();
            newBundleInf6.Write(writer);

            uint infoSize = (uint)(assetDataPos - bundleInfPos);

            writer.Position = 0;
            AssetBundleHeader06 newBundleHeader6 = new AssetBundleHeader06()
            {
                signature = bundleHeader6.signature,
                fileVersion = bundleHeader6.fileVersion,
                minPlayerVersion = bundleHeader6.minPlayerVersion,
                fileEngineVersion = bundleHeader6.fileEngineVersion,
                totalFileSize = finalSize,
                compressedSize = infoSize,
                decompressedSize = infoSize,
                flags = bundleHeader6.flags & unchecked((uint)~0x80) //unset info at end flag
            };
            newBundleHeader6.Write(writer);

            return true;
        }

        //-todo, use a faster custom bundle decompressor. currently a copy paste of unity studio's
        public bool Unpack(AssetsFileReader reader, AssetsFileWriter writer)
        {
            reader.Position = 0;
            if (Read(reader, true))
            {
                reader.Position = bundleHeader6.GetBundleInfoOffset();
                MemoryStream blocksInfoStream;
                AssetsFileReader memReader;
                int compressedSize = (int)bundleHeader6.compressedSize;
                switch (bundleHeader6.flags & 0x3F)
                {
                    case 1:
                        using (MemoryStream mstream = new MemoryStream(reader.ReadBytes(compressedSize)))
                        {
                            blocksInfoStream = SevenZipHelper.StreamDecompress(mstream);
                        }
                        break;
                    case 2:
                    case 3:
                        byte[] uncompressedBytes = new byte[bundleHeader6.decompressedSize];
                        using (MemoryStream mstream = new MemoryStream(reader.ReadBytes(compressedSize)))
                        {
                            var decoder = new Lz4DecoderStream(mstream);
                            decoder.Read(uncompressedBytes, 0, (int)bundleHeader6.decompressedSize);
                            decoder.Dispose();
                        }
                        blocksInfoStream = new MemoryStream(uncompressedBytes);
                        break;
                    default:
                        blocksInfoStream = null;
                        break;
                }
                if ((bundleHeader6.flags & 0x3F) != 0)
                {
                    using (memReader = new AssetsFileReader(blocksInfoStream))
                    {
                        bundleInf6.Read(0, memReader);
                    }
                }
                reader.Position = bundleHeader6.GetFileDataOffset();
                byte[][] blocksData = new byte[bundleInf6.blockCount][];
                for (int i = 0; i < bundleInf6.blockCount; i++)
                {
                    AssetBundleBlockInfo06 info = bundleInf6.blockInf[i];
                    byte[] data = reader.ReadBytes((int)info.compressedSize);
                    switch (info.flags & 0x3F)
                    {
                        case 0:
                            blocksData[i] = data;
                            break;
                        case 1:
                            blocksData[i] = new byte[info.decompressedSize];
                            using (MemoryStream mstream = new MemoryStream(data))
                            {
                                MemoryStream decoder = SevenZipHelper.StreamDecompress(mstream, info.decompressedSize);
                                decoder.Read(blocksData[i], 0, (int)info.decompressedSize);
                                decoder.Dispose();
                            }
                            break;
                        case 2:
                        case 3:
                            blocksData[i] = new byte[info.decompressedSize];
                            using (MemoryStream mstream = new MemoryStream(data))
                            {
                                var decoder = new Lz4DecoderStream(mstream);
                                decoder.Read(blocksData[i], 0, (int)info.decompressedSize);
                                decoder.Dispose();
                            }
                            break;
                    }
                }
                AssetBundleHeader06 newBundleHeader6 = new AssetBundleHeader06()
                {
                    signature = bundleHeader6.signature,
                    fileVersion = bundleHeader6.fileVersion,
                    minPlayerVersion = bundleHeader6.minPlayerVersion,
                    fileEngineVersion = bundleHeader6.fileEngineVersion,
                    totalFileSize = 0,
                    compressedSize = bundleHeader6.decompressedSize,
                    decompressedSize = bundleHeader6.decompressedSize,
                    flags = bundleHeader6.flags & 0x40 //set compression and block position to 0
                };
                long fileSize = newBundleHeader6.GetFileDataOffset();
                for (int i = 0; i < bundleInf6.blockCount; i++)
                    fileSize += bundleInf6.blockInf[i].decompressedSize;
                newBundleHeader6.totalFileSize = fileSize;
                AssetBundleBlockAndDirectoryList06 newBundleInf6 = new AssetBundleBlockAndDirectoryList06()
                {
                    checksumLow = 0, //-todo, figure out how to make real checksums, uabe sets these to 0 too
                    checksumHigh = 0,
                    blockCount = bundleInf6.blockCount,
                    directoryCount = bundleInf6.directoryCount
                };
                newBundleInf6.blockInf = new AssetBundleBlockInfo06[newBundleInf6.blockCount];
                for (int i = 0; i < newBundleInf6.blockCount; i++)
                {
                    newBundleInf6.blockInf[i] = new AssetBundleBlockInfo06()
                    {
                        compressedSize = bundleInf6.blockInf[i].decompressedSize,
                        decompressedSize = bundleInf6.blockInf[i].decompressedSize,
                        flags = (ushort)(bundleInf6.blockInf[i].flags & 0xC0) //set compression to none
                    };
                }
                newBundleInf6.dirInf = new AssetBundleDirectoryInfo06[newBundleInf6.directoryCount];
                for (int i = 0; i < newBundleInf6.directoryCount; i++)
                {
                    newBundleInf6.dirInf[i] = new AssetBundleDirectoryInfo06()
                    {
                        offset = bundleInf6.dirInf[i].offset,
                        decompressedSize = bundleInf6.dirInf[i].decompressedSize,
                        flags = bundleInf6.dirInf[i].flags,
                        name = bundleInf6.dirInf[i].name
                    };
                }
                newBundleHeader6.Write(writer);
                newBundleInf6.Write(writer);
                for (int i = 0; i < newBundleInf6.blockCount; i++)
                {
                    writer.Write(blocksData[i]);
                }
                return true;
            }
            return false;
        }
        ///public bool Pack(AssetsFileReader reader, AssetsFileWriter writer);
        public bool IsAssetsFile(AssetsFileReader reader, AssetBundleDirectoryInfo06 entry)
        {
            //todo - not fully implemented
            long offset = bundleHeader6.GetFileDataOffset() + entry.offset;
            if (entry.decompressedSize < 0x20)
                return false;

            reader.Position = offset;
            string possibleBundleHeader = reader.ReadStringLength(7);
            if (possibleBundleHeader == "UnityFS")
                return false;

            reader.Position = offset + 0x08;
            int possibleFormat = reader.ReadInt32();
            if (possibleFormat > 99)
                return false;

            reader.Position = offset + 0x14;

            string possibleVersion = "";
            char curChar;
            while (reader.Position < reader.BaseStream.Length && (curChar = (char)reader.ReadByte()) != 0x00)
            {
                possibleVersion += curChar;
                if (possibleVersion.Length > 0xFF)
                {
                    return false;
                }
            }

            string emptyVersion = Regex.Replace(possibleVersion, "[a-zA-Z0-9\\.]", "");
            string fullVersion = Regex.Replace(possibleVersion, "[^a-zA-Z0-9\\.]", "");
            return emptyVersion == "" && fullVersion.Length > 0;
        }
    }
}
