﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Data.Analysis
{
    /// <summary>
    /// PrimitiveDataFrameColumnContainer is just a store for the column data. APIs that want to change the data must be defined in PrimitiveDataFrameColumn
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal partial class PrimitiveColumnContainer<T> : IEnumerable<T?>
        where T : unmanaged
    {
        public IList<ReadOnlyDataFrameBuffer<T>> Buffers = new List<ReadOnlyDataFrameBuffer<T>>();

        // To keep the mapping simple, each buffer is mapped 1v1 to a nullBitMapBuffer
        // A set bit implies a valid value. An unset bit => null value
        public IList<ReadOnlyDataFrameBuffer<byte>> NullBitMapBuffers = new List<ReadOnlyDataFrameBuffer<byte>>();

        // Need a way to differentiate between columns initialized with default values and those with null values in SetValidityBit
        internal bool _modifyNullCountWhileIndexing = true;

        public PrimitiveColumnContainer(T[] values)
        {
            values = values ?? throw new ArgumentNullException(nameof(values));
            long length = values.LongLength;
            DataFrameBuffer<T> curBuffer;
            if (Buffers.Count == 0)
            {
                curBuffer = new DataFrameBuffer<T>();
                Buffers.Add(curBuffer);
                NullBitMapBuffers.Add(new DataFrameBuffer<byte>());
            }
            else
            {
                curBuffer = (DataFrameBuffer<T>)Buffers[Buffers.Count - 1];
            }
            for (long i = 0; i < length; i++)
            {
                if (curBuffer.Length == ReadOnlyDataFrameBuffer<T>.MaxCapacity)
                {
                    curBuffer = new DataFrameBuffer<T>();
                    Buffers.Add(curBuffer);
                    NullBitMapBuffers.Add(new DataFrameBuffer<byte>());
                }
                curBuffer.Append(values[i]);
                SetValidityBit(Length, true);
                Length++;
            }
        }

        public PrimitiveColumnContainer(IEnumerable<T> values)
        {
            values = values ?? throw new ArgumentNullException(nameof(values));
            foreach (T value in values)
            {
                Append(value);
            }
        }
        public PrimitiveColumnContainer(IEnumerable<T?> values)
        {
            values = values ?? throw new ArgumentNullException(nameof(values));
            foreach (T? value in values)
            {
                Append(value);
            }
        }

        public PrimitiveColumnContainer(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<byte> nullBitMap, int length, int nullCount)
        {
            ReadOnlyDataFrameBuffer<T> dataBuffer;
            if (buffer.IsEmpty)
            {
                DataFrameBuffer<T> mutableBuffer = new DataFrameBuffer<T>();
                mutableBuffer.EnsureCapacity(length);
                mutableBuffer.Length = length;
                mutableBuffer.RawSpan.Fill(default(T));
                dataBuffer = mutableBuffer;
            }
            else
            {
                dataBuffer = new ReadOnlyDataFrameBuffer<T>(buffer, length);
            }
            Buffers.Add(dataBuffer);

            int bitMapBufferLength = (length + 7) / 8;
            ReadOnlyDataFrameBuffer<byte> nullDataFrameBuffer;
            if (nullBitMap.IsEmpty)
            {
                if (nullCount != 0)
                {
                    throw new ArgumentNullException(Strings.InconsistentNullBitMapAndNullCount, nameof(nullBitMap));
                }
                if (!buffer.IsEmpty)
                {
                    // Create a new bitMap with all the bits up to length set
                    var bitMap = new byte[bitMapBufferLength];
                    bitMap.AsSpan().Fill(255);
                    int lastByte = 1 << (length - (bitMapBufferLength - 1) * 8);
                    bitMap[bitMapBufferLength - 1] = (byte)(lastByte - 1);
                    nullDataFrameBuffer = new DataFrameBuffer<byte>(bitMap, bitMapBufferLength);
                }
                else
                {
                    nullDataFrameBuffer = new DataFrameBuffer<byte>();
                }
            }
            else
            {
                if (nullBitMap.Length < bitMapBufferLength)
                {
                    throw new ArgumentException(Strings.InconsistentNullBitMapAndLength, nameof(nullBitMap));
                }
                nullDataFrameBuffer = new ReadOnlyDataFrameBuffer<byte>(nullBitMap, bitMapBufferLength);
            }
            NullBitMapBuffers.Add(nullDataFrameBuffer);
            Length = length;
            NullCount = nullCount;
        }

        public PrimitiveColumnContainer(long length = 0)
        {
            AppendMany(null, length);
        }

        public void Resize(long length)
        {
            if (length < Length)
                throw new ArgumentException(Strings.CannotResizeDown, nameof(length));
            AppendMany(default, length - Length);
        }

        public void Append(T? value)
        {
            if (Buffers.Count == 0)
            {
                Buffers.Add(new DataFrameBuffer<T>());
                NullBitMapBuffers.Add(new DataFrameBuffer<byte>());
            }

            if (Buffers[Buffers.Count - 1].Length == ReadOnlyDataFrameBuffer<T>.MaxCapacity)
            {
                Buffers.Add(new DataFrameBuffer<T>());
                NullBitMapBuffers.Add(new DataFrameBuffer<byte>());
            }

            DataFrameBuffer<T> mutableLastBuffer = Buffers.GetOrCreateMutable(Buffers.Count - 1);
            mutableLastBuffer.Append(value ?? default);
            SetValidityBit(Length, value.HasValue);
            Length++;
        }

        public void AppendMany(T? value, long count)
        {
            if (!value.HasValue)
            {
                NullCount += count;
            }

            var remaining = count;
            while (remaining > 0)
            {
                if (Buffers.Count == 0)
                {
                    Buffers.Add(new DataFrameBuffer<T>());
                    NullBitMapBuffers.Add(new DataFrameBuffer<byte>());
                }

                if (Buffers[Buffers.Count - 1].Length == ReadOnlyDataFrameBuffer<T>.MaxCapacity)
                {
                    Buffers.Add(new DataFrameBuffer<T>());
                    NullBitMapBuffers.Add(new DataFrameBuffer<byte>());
                }

                DataFrameBuffer<T> mutableLastBuffer = Buffers.GetOrCreateMutable(Buffers.Count - 1);
                int allocatable = (int)Math.Min(remaining, ReadOnlyDataFrameBuffer<T>.MaxCapacity);
                mutableLastBuffer.EnsureCapacity(allocatable);

                DataFrameBuffer<byte> lastNullBitMapBuffer = NullBitMapBuffers.GetOrCreateMutable(NullBitMapBuffers.Count - 1);
                int nullBufferAllocatable = (allocatable + 7) / 8;
                lastNullBitMapBuffer.EnsureCapacity(nullBufferAllocatable);


                mutableLastBuffer.Length += allocatable;
                lastNullBitMapBuffer.Length += nullBufferAllocatable;
                Length += allocatable;

                if (value.HasValue)
                {
                    mutableLastBuffer.RawSpan.Slice(mutableLastBuffer.Length - allocatable, allocatable).Fill(value ?? default);

                    _modifyNullCountWhileIndexing = false;
                    for (long i = Length - allocatable; i < Length; i++)
                    {
                        SetValidityBit(i, value.HasValue);
                    }
                    _modifyNullCountWhileIndexing = true;
                }


                remaining -= allocatable;
            }
        }

        public void ApplyElementwise(Func<T?, long, T?> func)
        {
            var bufferMaxCapacity = ReadOnlyDataFrameBuffer<T>.MaxCapacity;
            for (int b = 0; b < Buffers.Count; b++)
            {
                long prevLength = checked(bufferMaxCapacity * b);

                Span<T> mutableBuffer = Buffers.GetOrCreateMutable(b).Span;
                Span<byte> mutableNullBitMapBuffer = NullBitMapBuffers.GetOrCreateMutable(b).Span;

                for (int i = 0; i < mutableBuffer.Length; i++)
                {
                    long curIndex = i + prevLength;
                    bool isValid = IsValid(mutableNullBitMapBuffer, i);
                    T? value = func(isValid ? mutableBuffer[i] : null, curIndex);
                    mutableBuffer[i] = value.GetValueOrDefault();
                    SetValidityBit(mutableNullBitMapBuffer, i, value != null);
                }
            }
        }

        public void Apply<TResult>(Func<T?, TResult?> func, PrimitiveColumnContainer<TResult> resultContainer)
            where TResult : unmanaged
        {
            var bufferMaxCapacity = ReadOnlyDataFrameBuffer<T>.MaxCapacity;
            for (int b = 0; b < Buffers.Count; b++)
            {
                long prevLength = checked(bufferMaxCapacity * b);
                var sourceBuffer = Buffers[b];
                var sourceNullBitMap = NullBitMapBuffers[b].ReadOnlySpan;

                Span<TResult> mutableResultBuffer = resultContainer.Buffers.GetOrCreateMutable(b).Span;
                Span<byte> mutableResultNullBitMapBuffers = resultContainer.NullBitMapBuffers.GetOrCreateMutable(b).Span;

                for (int i = 0; i < sourceBuffer.Length; i++)
                {
                    bool isValid = IsValid(sourceNullBitMap, i);
                    TResult? value = func(isValid ? sourceBuffer[i] : null);
                    mutableResultBuffer[i] = value.GetValueOrDefault();
                    resultContainer.SetValidityBit(mutableResultNullBitMapBuffers, i, value != null);
                }
            }
        }

        // Faster to use when we already have a span since it avoids indexing
        public bool IsValid(ReadOnlySpan<byte> bitMapBufferSpan, int index)
        {
            int nullBitMapSpanIndex = index / 8;
            byte thisBitMap = bitMapBufferSpan[nullBitMapSpanIndex];
            return IsBitSet(thisBitMap, index);
        }

        public bool IsValid(long index) => NullCount == 0 || GetValidityBit(index);

        private byte SetBit(byte curBitMap, int index, bool value)
        {
            byte newBitMap;
            if (value)
            {
                newBitMap = (byte)(curBitMap | (byte)(1 << (index & 7))); //bit hack for index % 8
                if (_modifyNullCountWhileIndexing && ((curBitMap >> (index & 7)) & 1) == 0 && index < Length && NullCount > 0)
                {
                    // Old value was null.
                    NullCount--;
                }
            }
            else
            {
                if (_modifyNullCountWhileIndexing && ((curBitMap >> (index & 7)) & 1) == 1 && index < Length)
                {
                    // old value was NOT null and new value is null
                    NullCount++;
                }
                else if (_modifyNullCountWhileIndexing && index == Length)
                {
                    // New entry from an append
                    NullCount++;
                }
                newBitMap = (byte)(curBitMap & (byte)~(1 << (int)((uint)index & 7)));
            }
            return newBitMap;
        }

        // private function. Faster to use when we already have a span since it avoids indexing
        private void SetValidityBit(Span<byte> bitMapBufferSpan, int index, bool value)
        {
            int bitMapBufferIndex = (int)((uint)index / 8);
            Debug.Assert(bitMapBufferSpan.Length >= bitMapBufferIndex);
            byte curBitMap = bitMapBufferSpan[bitMapBufferIndex];
            byte newBitMap = SetBit(curBitMap, index, value);
            bitMapBufferSpan[bitMapBufferIndex] = newBitMap;
        }

        /// <summary>
        /// A null value has an unset bit
        /// A NON-null value has a set bit
        /// </summary>
        /// <param name="index"></param>
        /// <param name="value"></param>
        internal void SetValidityBit(long index, bool value)
        {
            if ((ulong)index > (ulong)Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            // First find the right bitMapBuffer
            int bitMapIndex = (int)(index / ReadOnlyDataFrameBuffer<T>.MaxCapacity);
            Debug.Assert(NullBitMapBuffers.Count > bitMapIndex);
            DataFrameBuffer<byte> bitMapBuffer = (DataFrameBuffer<byte>)NullBitMapBuffers[bitMapIndex];

            // Set the bit
            index -= bitMapIndex * ReadOnlyDataFrameBuffer<T>.MaxCapacity;
            int bitMapBufferIndex = (int)((uint)index / 8);
            Debug.Assert(bitMapBuffer.Length >= bitMapBufferIndex);
            if (bitMapBuffer.Length == bitMapBufferIndex)
                bitMapBuffer.Append(0);
            SetValidityBit(bitMapBuffer.Span, (int)index, value);
        }

        private bool IsBitSet(byte curBitMap, int index)
        {
            return ((curBitMap >> (index & 7)) & 1) != 0;
        }

        private bool GetValidityBit(long index)
        {
            if ((uint)index >= Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            // First find the right bitMapBuffer
            int bitMapIndex = (int)(index / ReadOnlyDataFrameBuffer<T>.MaxCapacity);
            Debug.Assert(NullBitMapBuffers.Count > bitMapIndex);
            ReadOnlyDataFrameBuffer<byte> bitMapBuffer = NullBitMapBuffers[bitMapIndex];

            // Get the bit
            index -= bitMapIndex * ReadOnlyDataFrameBuffer<T>.MaxCapacity;
            int bitMapBufferIndex = (int)((uint)index / 8);
            Debug.Assert(bitMapBuffer.Length > bitMapBufferIndex);
            byte curBitMap = bitMapBuffer[bitMapBufferIndex];
            return IsBitSet(curBitMap, (int)index);
        }

        public long Length;

        public long NullCount;
        public int GetArrayContainingRowIndex(long rowIndex)
        {
            if (rowIndex >= Length)
            {
                throw new ArgumentOutOfRangeException(Strings.ColumnIndexOutOfRange, nameof(rowIndex));
            }
            return (int)(rowIndex / ReadOnlyDataFrameBuffer<T>.MaxCapacity);
        }

        internal int MaxRecordBatchLength(long startIndex)
        {
            if (Length == 0)
                return 0;
            int arrayIndex = GetArrayContainingRowIndex(startIndex);
            startIndex = startIndex - arrayIndex * ReadOnlyDataFrameBuffer<T>.MaxCapacity;
            return Buffers[arrayIndex].Length - (int)startIndex;
        }

        internal ReadOnlyMemory<byte> GetValueBuffer(long startIndex)
        {
            int arrayIndex = GetArrayContainingRowIndex(startIndex);
            return Buffers[arrayIndex].ReadOnlyBuffer;
        }

        internal ReadOnlyMemory<byte> GetNullBuffer(long startIndex)
        {
            int arrayIndex = GetArrayContainingRowIndex(startIndex);
            return NullBitMapBuffers[arrayIndex].ReadOnlyBuffer;
        }

        public IReadOnlyList<T?> this[long startIndex, int length]
        {
            get
            {
                var ret = new List<T?>(length);
                long endIndex = Math.Min(Length, startIndex + length);
                for (long i = startIndex; i < endIndex; i++)
                {
                    ret.Add(this[i]);
                }
                return ret;
            }
        }

        public T? this[long rowIndex]
        {
            get
            {
                if (!IsValid(rowIndex))
                {
                    return null;
                }
                int arrayIndex = GetArrayContainingRowIndex(rowIndex);
                rowIndex = rowIndex - arrayIndex * ReadOnlyDataFrameBuffer<T>.MaxCapacity;
                return Buffers[arrayIndex][(int)rowIndex];
            }
            set
            {
                int arrayIndex = GetArrayContainingRowIndex(rowIndex);
                rowIndex = rowIndex - arrayIndex * ReadOnlyDataFrameBuffer<T>.MaxCapacity;

                Buffers.GetOrCreateMutable(arrayIndex);
                NullBitMapBuffers.GetOrCreateMutable(arrayIndex);

                if (value.HasValue)
                {
                    Buffers[arrayIndex][(int)rowIndex] = value.Value;
                    SetValidityBit(rowIndex, true);
                }
                else
                {
                    Buffers[arrayIndex][(int)rowIndex] = default;
                    SetValidityBit(rowIndex, false);
                }
            }
        }

        public IEnumerator<T?> GetEnumerator()
        {
            for (long i = 0; i < Length; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Length; i++)
            {
                T? value = this[i];
                if (value.HasValue)
                {
                    sb.Append(this[i]).Append(" ");
                }
                else
                {
                    sb.Append("null").Append(" ");
                }
                // Can this run out of memory? Just being safe here
                if (sb.Length > 1000)
                {
                    sb.Append("...");
                    break;
                }
            }
            return sb.ToString();
        }

        private List<ReadOnlyDataFrameBuffer<byte>> CloneNullBitMapBuffers()
        {
            List<ReadOnlyDataFrameBuffer<byte>> ret = new List<ReadOnlyDataFrameBuffer<byte>>();
            foreach (ReadOnlyDataFrameBuffer<byte> buffer in NullBitMapBuffers)
            {
                DataFrameBuffer<byte> newBuffer = new DataFrameBuffer<byte>();
                ret.Add(newBuffer);
                ReadOnlySpan<byte> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(span[i]);
                }
            }
            return ret;
        }

        public PrimitiveColumnContainer<T> Clone<U>(PrimitiveColumnContainer<U> mapIndices, Type type, bool invertMapIndices = false)
            where U : unmanaged
        {
            ReadOnlySpan<T> thisSpan = Buffers[0].ReadOnlySpan;
            ReadOnlySpan<byte> thisNullBitMapSpan = NullBitMapBuffers[0].ReadOnlySpan;
            long minRange = 0;
            long maxRange = DataFrameBuffer<T>.MaxCapacity;
            long maxCapacity = maxRange;
            PrimitiveColumnContainer<T> ret = new PrimitiveColumnContainer<T>(mapIndices.Length);
            for (int b = 0; b < mapIndices.Buffers.Count; b++)
            {
                int index = b;
                if (invertMapIndices)
                    index = mapIndices.Buffers.Count - 1 - b;

                ReadOnlyDataFrameBuffer<U> buffer = mapIndices.Buffers[index];
                ReadOnlySpan<byte> mapIndicesNullBitMapSpan = mapIndices.NullBitMapBuffers[index].ReadOnlySpan;
                ReadOnlySpan<U> mapIndicesSpan = buffer.ReadOnlySpan;
                ReadOnlySpan<long> mapIndicesLongSpan = default;
                ReadOnlySpan<int> mapIndicesIntSpan = default;
                DataFrameBuffer<T> mutableBuffer = DataFrameBuffer<T>.GetMutableBuffer(ret.Buffers[index]);
                ret.Buffers[index] = mutableBuffer;
                Span<T> retSpan = mutableBuffer.Span;
                DataFrameBuffer<byte> mutableNullBuffer = DataFrameBuffer<byte>.GetMutableBuffer(ret.NullBitMapBuffers[index]);
                ret.NullBitMapBuffers[index] = mutableNullBuffer;
                Span<byte> retNullBitMapSpan = mutableNullBuffer.Span;
                if (type == typeof(long))
                {
                    mapIndicesLongSpan = MemoryMarshal.Cast<U, long>(mapIndicesSpan);
                }
                if (type == typeof(int))
                {
                    mapIndicesIntSpan = MemoryMarshal.Cast<U, int>(mapIndicesSpan);
                }
                for (int i = 0; i < buffer.Length; i++)
                {
                    int spanIndex = i;
                    if (invertMapIndices)
                        spanIndex = buffer.Length - 1 - i;

                    long mapRowIndex = mapIndicesIntSpan.IsEmpty ? mapIndicesLongSpan[spanIndex] : mapIndicesIntSpan[spanIndex];
                    bool mapRowIndexIsValid = mapIndices.IsValid(mapIndicesNullBitMapSpan, spanIndex);
                    if (mapRowIndexIsValid && (mapRowIndex < minRange || mapRowIndex >= maxRange))
                    {
                        int bufferIndex = (int)(mapRowIndex / maxCapacity);
                        thisSpan = Buffers[bufferIndex].ReadOnlySpan;
                        thisNullBitMapSpan = NullBitMapBuffers[bufferIndex].ReadOnlySpan;
                        minRange = bufferIndex * maxCapacity;
                        maxRange = (bufferIndex + 1) * maxCapacity;
                    }
                    T value = default;
                    bool isValid = false;
                    if (mapRowIndexIsValid)
                    {
                        mapRowIndex -= minRange;
                        value = thisSpan[(int)mapRowIndex];
                        isValid = IsValid(thisNullBitMapSpan, (int)mapRowIndex);
                    }

                    retSpan[i] = isValid ? value : default;
                    ret.SetValidityBit(retNullBitMapSpan, i, isValid);
                }
            }
            return ret;
        }

        public PrimitiveColumnContainer<T> Clone()
        {
            var ret = new PrimitiveColumnContainer<T>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                DataFrameBuffer<T> newBuffer = new DataFrameBuffer<T>();
                ret.Buffers.Add(newBuffer);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                ret.Length += buffer.Length;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(span[i]);
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<bool> CloneAsBoolContainer()
        {
            var ret = new PrimitiveColumnContainer<bool>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                DataFrameBuffer<bool> newBuffer = new DataFrameBuffer<bool>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                if (typeof(T) == typeof(bool))
                {
                    var localBuffer = buffer;
                    ReadOnlyDataFrameBuffer<bool> boolLocalBuffer = Unsafe.As<ReadOnlyDataFrameBuffer<T>, ReadOnlyDataFrameBuffer<bool>>(ref localBuffer);
                    boolLocalBuffer.ReadOnlySpan.TryCopyTo(newBuffer.RawSpan);
                }
                else
                {
                    newBuffer.Span.Fill(false);
                }
                newBuffer.Length = buffer.Length;
                ret.Length += buffer.Length;
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<byte> CloneAsByteContainer()
        {
            var ret = new PrimitiveColumnContainer<byte>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<byte> newBuffer = new DataFrameBuffer<byte>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(ByteConverter<T>.Instance.GetByte(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<sbyte> CloneAsSByteContainer()
        {
            var ret = new PrimitiveColumnContainer<sbyte>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<sbyte> newBuffer = new DataFrameBuffer<sbyte>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(SByteConverter<T>.Instance.GetSByte(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<double> CloneAsDoubleContainer()
        {
            var ret = new PrimitiveColumnContainer<double>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<double> newBuffer = new DataFrameBuffer<double>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(DoubleConverter<T>.Instance.GetDouble(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<decimal> CloneAsDecimalContainer()
        {
            var ret = new PrimitiveColumnContainer<decimal>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<decimal> newBuffer = new DataFrameBuffer<decimal>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(DecimalConverter<T>.Instance.GetDecimal(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<short> CloneAsShortContainer()
        {
            var ret = new PrimitiveColumnContainer<short>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<short> newBuffer = new DataFrameBuffer<short>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(Int16Converter<T>.Instance.GetInt16(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<ushort> CloneAsUShortContainer()
        {
            var ret = new PrimitiveColumnContainer<ushort>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<ushort> newBuffer = new DataFrameBuffer<ushort>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(UInt16Converter<T>.Instance.GetUInt16(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<int> CloneAsIntContainer()
        {
            var ret = new PrimitiveColumnContainer<int>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<int> newBuffer = new DataFrameBuffer<int>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(Int32Converter<T>.Instance.GetInt32(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<uint> CloneAsUIntContainer()
        {
            var ret = new PrimitiveColumnContainer<uint>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<uint> newBuffer = new DataFrameBuffer<uint>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(UInt32Converter<T>.Instance.GetUInt32(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<long> CloneAsLongContainer()
        {
            var ret = new PrimitiveColumnContainer<long>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<long> newBuffer = new DataFrameBuffer<long>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(Int64Converter<T>.Instance.GetInt64(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<ulong> CloneAsULongContainer()
        {
            var ret = new PrimitiveColumnContainer<ulong>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<ulong> newBuffer = new DataFrameBuffer<ulong>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(UInt64Converter<T>.Instance.GetUInt64(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }

        internal PrimitiveColumnContainer<float> CloneAsFloatContainer()
        {
            var ret = new PrimitiveColumnContainer<float>();
            foreach (ReadOnlyDataFrameBuffer<T> buffer in Buffers)
            {
                ret.Length += buffer.Length;
                DataFrameBuffer<float> newBuffer = new DataFrameBuffer<float>();
                ret.Buffers.Add(newBuffer);
                newBuffer.EnsureCapacity(buffer.Length);
                ReadOnlySpan<T> span = buffer.ReadOnlySpan;
                for (int i = 0; i < span.Length; i++)
                {
                    newBuffer.Append(SingleConverter<T>.Instance.GetSingle(span[i]));
                }
            }
            ret.NullBitMapBuffers = CloneNullBitMapBuffers();
            ret.NullCount = NullCount;
            return ret;
        }
    }
}
