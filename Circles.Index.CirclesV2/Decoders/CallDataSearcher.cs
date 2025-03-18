using System.Buffers.Binary;

namespace Circles.Index.CirclesV2.Decoders
{
    public static class CallDataSearcher
    {
        /// <summary>
        /// Finds the 'selector' in the data and returns its position.
        /// </summary>
        /// <param name="data">The data to search in</param>
        /// <param name="selector">The selector to search for (first four bytes of the hashed signature encoded as uint32)</param>
        /// <returns></returns>
        public static int[] FindOccurrences(this Memory<byte> data, uint selector)
        {
            List<int> result = new List<int>();
            Span<byte> span = data.Span;
            for (int i = 0; i <= span.Length - 4; i++)
            {
                uint chunk = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i, 4));
                if (chunk == selector)
                {
                    result.Add(i);
                }
            }

            return result.ToArray();
        }
    }
}