

namespace Img2Ffu.Writer
{
    internal static class ImageSplitter
    {
        private static GPT GetGPT(Stream stream, uint BlockSize, uint sectorSize, ILogging Logging)
        {
            byte[] GPTBuffer = new byte[BlockSize];
            _ = stream.Read(GPTBuffer);

            uint requiredGPTBufferSize = Img2Ffu.GPT.GetGPTSize(GPTBuffer, sectorSize);
            if (BlockSize < requiredGPTBufferSize)
            {
                string errorMessage = $"The Block size is too small to contain the GPT, the GPT is {requiredGPTBufferSize} bytes long, the Block size is {BlockSize} bytes long";
                Logging.Log(errorMessage, ILoggingLevel.Error);
                throw new Exception(errorMessage);
            }

            uint sectorsInABlock = BlockSize / sectorSize;

            GPT GPT = new(GPTBuffer, sectorSize);

            IOrderedEnumerable<GPT.Partition> orderedGPTPartitions = GPT.Partitions.OrderBy(x => x.FirstSector);

            if (BlockSize > requiredGPTBufferSize && orderedGPTPartitions.Any(x => x.FirstSector < sectorsInABlock))
            {
                GPT.Partition conflictingPartition = orderedGPTPartitions.First(x => x.FirstSector < sectorsInABlock);

                string errorMessage = $"The Block size is too big to contain only the GPT, the GPT is {requiredGPTBufferSize} bytes long, the Block size is {BlockSize} bytes long. The overlapping partition is {conflictingPartition.Name} at {conflictingPartition.FirstSector * sectorSize}";
                Logging.Log(errorMessage, ILoggingLevel.Error);
                throw new Exception(errorMessage);
            }

            return GPT;
        }

        internal static (FlashPart[], List<GPT.Partition> partitions) GetImageSlices(Stream stream, uint BlockSize, string[] ExcludedPartitionNames, uint sectorSize, ILogging Logging)
        {
            GPT GPT = GetGPT(stream, BlockSize, sectorSize, Logging);
            uint sectorsInABlock = BlockSize / sectorSize;

            List<FlashPart> flashParts = FlashPartFactory.GetFlashParts(GPT, stream, BlockSize, ExcludedPartitionNames, sectorSize, Logging);

            FlashPart[] finalFlashParts = [.. flashParts];

            Logging.Log("");
            Logging.Log("Final Flash Parts");
            Logging.Log("");
            PrintFlashParts(finalFlashParts, sectorSize, BlockSize, Logging);
            Logging.Log("");

            foreach (FlashPart flashPart in finalFlashParts)
            {
                ulong totalSectors = (ulong)flashPart.Stream.Length / sectorSize;
                ulong firstSector = flashPart.StartLocation / sectorSize;
                ulong lastSector = firstSector + totalSectors - 1;

                if (firstSector % sectorsInABlock != 0)
                {
                    string errorMessage = $"- The stream doesn't start on a Block boundary (Total Sectors: {totalSectors} - First Sector: {firstSector} - Last Sector: {lastSector}) - Overflow: {firstSector % sectorsInABlock}, a Block is {sectorsInABlock} sectors";
                    Logging.Log(errorMessage, ILoggingLevel.Error);
                    throw new Exception(errorMessage);
                }

                if ((lastSector + 1) % sectorsInABlock != 0)
                {
                    string errorMessage = $"- The stream doesn't end on a Block boundary (Total Sectors: {totalSectors} - First Sector: {firstSector} - Last Sector: {lastSector}) - Overflow: {(lastSector + 1) % sectorsInABlock}, a Block is {sectorsInABlock} sectors";
                    Logging.Log(errorMessage, ILoggingLevel.Error);
                    throw new Exception(errorMessage);
                }
            }

            return (finalFlashParts, GPT.Partitions);
        }

        private static void PrintFlashParts(FlashPart[] finalFlashParts, uint sectorSize, uint BlockSize, ILogging Logging)
        {
            for (int i = 0; i < finalFlashParts.Length; i++)
            {
                FlashPart flashPart = finalFlashParts[i];
                PrintFlashPart(flashPart, sectorSize, BlockSize, $"FlashPart[{i}]", Logging);
            }
        }

        private static void PrintFlashPart(FlashPart flashPart, uint sectorSize, uint BlockSize, string name, ILogging Logging)
        {
            uint sectorsInABlock = BlockSize / sectorSize;

            ulong totalSectors = (ulong)flashPart.Stream.Length / sectorSize;
            ulong firstSector = flashPart.StartLocation / sectorSize;
            ulong lastSector = firstSector + totalSectors - 1;

            Logging.Log($"{name} - {firstSector}s - {lastSector}s - {totalSectors}s - {totalSectors / (double)sectorsInABlock}c", ILoggingLevel.Information);
        }
    }
}