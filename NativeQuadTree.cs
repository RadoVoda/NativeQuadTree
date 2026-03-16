using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace NativeQuadTree
{
	public struct QuadElement<T> where T : unmanaged
	{
		public int2 pos;
		public T element;
	}

	internal struct QuadNode
	{
		public int index;
		public short count;
		public bool leaf;
	}

    [Serializable]
    public readonly struct AABB2D
    {
        public readonly int2 Center;
        public readonly int2 Extents;
        public int2 Size => Extents * 2;
        public int2 Min => Center - Extents;
        public int2 Max => Center + Extents;

        public AABB2D(int2 center, int2 extents)
        {
            Center = center;
            Extents = extents;
        }

        public AABB2D(int width, int height)
        {
            Center = new int2(width >> 1, height >> 1);
            Extents = new int2(width - Center.x, height - Center.y);
        }

        public bool Contains(int2 point)
        {
            return point[0] >= Center[0] - Extents[0] &&
                    point[0] <= Center[0] + Extents[0] &&
                    point[1] >= Center[1] - Extents[1] &&
                    point[1] <= Center[1] + Extents[1];
        }

        public bool Contains(AABB2D b)
        {
            return Contains(b.Center + new int2(-b.Extents.x, -b.Extents.y)) &&
                   Contains(b.Center + new int2(-b.Extents.x, b.Extents.y)) &&
                   Contains(b.Center + new int2(b.Extents.x, -b.Extents.y)) &&
                   Contains(b.Center + new int2(b.Extents.x, b.Extents.y));
        }

        public bool Intersects(AABB2D b)
        {
            return (math.abs(Center[0] - b.Center[0]) < (Extents[0] + b.Extents[0])) &&
                   (math.abs(Center[1] - b.Center[1]) < (Extents[1] + b.Extents[1]));
        }

        public AABB2D GetChildBounds(int childIndex)
        {
            int2 half = Extents;
            half += half & 1;
            half >>= 1;

            switch (childIndex)
            {
                case 0: return new AABB2D(new int2(Center.x - half.x, Center.y + half.y), half);
                case 1: return new AABB2D(new int2(Center.x + half.x, Center.y + half.y), half);
                case 2: return new AABB2D(new int2(Center.x - half.x, Center.y - half.y), half);
                case 3: return new AABB2D(new int2(Center.x + half.x, Center.y - half.y), half);
                default: throw new Exception();
            }
        }
    }

    /// <summary>
    /// A QuadTree aimed to be used with Burst, supports fast bulk insertion and querying.
    /// </summary>
    [NativeContainer]
	public unsafe partial struct NativeQuadTree<T> : IDisposable where T : unmanaged
	{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		// Safety
		AtomicSafetyHandle m_Safety;
        internal static readonly int s_staticSafetyId = AtomicSafetyHandle.NewStaticSafetyId<NativeQuadTree<T>>();
#endif
        [NativeDisableUnsafePtrRestriction]
		UnsafeList<QuadElement<T>>* elements;

		[NativeDisableUnsafePtrRestriction]
		UnsafeList<int>* lookup;

		[NativeDisableUnsafePtrRestriction]
		UnsafeList<QuadNode>* nodes;

		int elementsCount;
		int maxDepth;
		short maxLeafElements;
		AABB2D bounds;

		public bool IsCreated => nodes != null;

        public static readonly ushort[] MortonLookup = {
		//	0       1       100     101     10000   10001   10100   10101
			0x0000, 0x0001, 0x0004, 0x0005, 0x0010, 0x0011, 0x0014, 0x0015,
		//	1000000	1000001	1000100	1000101	1010000	1010001	1010100	1010101
			0x0040, 0x0041, 0x0044, 0x0045, 0x0050, 0x0051, 0x0054, 0x0055,
		// etc...
			0x0100, 0x0101, 0x0104, 0x0105, 0x0110, 0x0111, 0x0114, 0x0115,
            0x0140, 0x0141, 0x0144, 0x0145, 0x0150, 0x0151, 0x0154, 0x0155,
            0x0400, 0x0401, 0x0404, 0x0405, 0x0410, 0x0411, 0x0414, 0x0415,
            0x0440, 0x0441, 0x0444, 0x0445, 0x0450, 0x0451, 0x0454, 0x0455,
            0x0500, 0x0501, 0x0504, 0x0505, 0x0510, 0x0511, 0x0514, 0x0515,
            0x0540, 0x0541, 0x0544, 0x0545, 0x0550, 0x0551, 0x0554, 0x0555,
            0x1000, 0x1001, 0x1004, 0x1005, 0x1010, 0x1011, 0x1014, 0x1015,
            0x1040, 0x1041, 0x1044, 0x1045, 0x1050, 0x1051, 0x1054, 0x1055,
            0x1100, 0x1101, 0x1104, 0x1105, 0x1110, 0x1111, 0x1114, 0x1115,
            0x1140, 0x1141, 0x1144, 0x1145, 0x1150, 0x1151, 0x1154, 0x1155,
            0x1400, 0x1401, 0x1404, 0x1405, 0x1410, 0x1411, 0x1414, 0x1415,
            0x1440, 0x1441, 0x1444, 0x1445, 0x1450, 0x1451, 0x1454, 0x1455,
            0x1500, 0x1501, 0x1504, 0x1505, 0x1510, 0x1511, 0x1514, 0x1515,
            0x1540, 0x1541, 0x1544, 0x1545, 0x1550, 0x1551, 0x1554, 0x1555,
            0x4000, 0x4001, 0x4004, 0x4005, 0x4010, 0x4011, 0x4014, 0x4015,
            0x4040, 0x4041, 0x4044, 0x4045, 0x4050, 0x4051, 0x4054, 0x4055,
            0x4100, 0x4101, 0x4104, 0x4105, 0x4110, 0x4111, 0x4114, 0x4115,
            0x4140, 0x4141, 0x4144, 0x4145, 0x4150, 0x4151, 0x4154, 0x4155,
            0x4400, 0x4401, 0x4404, 0x4405, 0x4410, 0x4411, 0x4414, 0x4415,
            0x4440, 0x4441, 0x4444, 0x4445, 0x4450, 0x4451, 0x4454, 0x4455,
            0x4500, 0x4501, 0x4504, 0x4505, 0x4510, 0x4511, 0x4514, 0x4515,
            0x4540, 0x4541, 0x4544, 0x4545, 0x4550, 0x4551, 0x4554, 0x4555,
            0x5000, 0x5001, 0x5004, 0x5005, 0x5010, 0x5011, 0x5014, 0x5015,
            0x5040, 0x5041, 0x5044, 0x5045, 0x5050, 0x5051, 0x5054, 0x5055,
            0x5100, 0x5101, 0x5104, 0x5105, 0x5110, 0x5111, 0x5114, 0x5115,
            0x5140, 0x5141, 0x5144, 0x5145, 0x5150, 0x5151, 0x5154, 0x5155,
            0x5400, 0x5401, 0x5404, 0x5405, 0x5410, 0x5411, 0x5414, 0x5415,
            0x5440, 0x5441, 0x5444, 0x5445, 0x5450, 0x5451, 0x5454, 0x5455,
            0x5500, 0x5501, 0x5504, 0x5505, 0x5510, 0x5511, 0x5514, 0x5515,
            0x5540, 0x5541, 0x5544, 0x5545, 0x5550, 0x5551, 0x5554, 0x5555
        };

        public static readonly int[] DepthSizeLookup =
        {
            0,
            1,
            1+2*2,
            1+2*2+4*4,
            1+2*2+4*4+8*8,
            1+2*2+4*4+8*8+16*16,
            1+2*2+4*4+8*8+16*16+32*32,
            1+2*2+4*4+8*8+16*16+32*32+64*64,
            1+2*2+4*4+8*8+16*16+32*32+64*64+128*128,
            1+2*2+4*4+8*8+16*16+32*32+64*64+128*128+256*256,
        };

        public static readonly int[] DepthLookup =
        {
            0,
            2,
            4,
            8,
            16,
            32,
            64,
            128,
            256,
        };

        internal unsafe struct QuadTreeRangeQuery
        {
            NativeQuadTree<T> tree;
            UnsafeList<QuadElement<T>>* fastResults;
            AABB2D bounds;
            int count;

            public void Query(NativeQuadTree<T> tree, AABB2D bounds, NativeList<QuadElement<T>> results)
            {
                this.tree = tree;
                this.bounds = bounds;
                count = 0;

                // Get pointer to inner list data for faster writing
                fastResults = (UnsafeList<QuadElement<T>>*)NativeListUnsafeUtility.GetInternalListDataPtrUnchecked(ref results);
                RecursiveRangeQuery(tree.bounds, false, 1, 1);
                fastResults->Length = count;
            }

            public void RecursiveRangeQuery(AABB2D parentBounds, bool parentContained, int prevOffset, int depth)
            {
                if (count + 4 * tree.maxLeafElements > fastResults->Capacity)
                {
                    fastResults->Resize(math.max(fastResults->Capacity * 2, count + 4 * tree.maxLeafElements));
                }

                var depthSize = DepthSizeLookup[tree.maxDepth - depth + 1];

                for (int childIndex = 0; childIndex < 4; childIndex++)
                {
                    var childBounds = parentBounds.GetChildBounds(childIndex);
                    var contained = parentContained;

                    if (!contained)
                    {
                        if (bounds.Contains(childBounds))
                        {
                            contained = true;
                        }
                        else if (!bounds.Intersects(childBounds))
                        {
                            continue;
                        }
                    }

                    var at = prevOffset + childIndex * depthSize;

                    var elementCount = UnsafeUtility.ReadArrayElement<int>(tree.lookup->Ptr, at);

                    if (elementCount > tree.maxLeafElements && depth < tree.maxDepth)
                    {
                        RecursiveRangeQuery(childBounds, contained, at + 1, depth + 1);
                    }
                    else if (elementCount != 0)
                    {
                        var node = UnsafeUtility.ReadArrayElement<QuadNode>(tree.nodes->Ptr, at);

                        if (contained)
                        {
                            var index = (void*)((IntPtr)tree.elements->Ptr + node.index * UnsafeUtility.SizeOf<QuadElement<T>>());

                            UnsafeUtility.MemCpy((void*)((IntPtr)fastResults->Ptr + count * UnsafeUtility.SizeOf<QuadElement<T>>()),
                                index, node.count * UnsafeUtility.SizeOf<QuadElement<T>>());
                            count += node.count;
                        }
                        else
                        {
                            for (int k = 0; k < node.count; k++)
                            {
                                var element = UnsafeUtility.ReadArrayElement<QuadElement<T>>(tree.elements->Ptr, node.index + k);
                                if (bounds.Contains(element.pos))
                                {
                                    UnsafeUtility.WriteArrayElement(fastResults->Ptr, count++, element);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Create a new QuadTree.
        /// - Ensure the bounds are not way bigger than needed, otherwise the buckets are very off. Probably best to calculate bounds
        /// - The higher the depth, the larger the overhead, it especially goes up at a depth of 7/8
        /// </summary>
        public NativeQuadTree(AABB2D bounds, Allocator allocator = Allocator.Temp, int maxDepth = 6, short maxLeafElements = 16, int initialElementsCapacity = 256) : this()
		{
			this.bounds = bounds;
			this.maxDepth = maxDepth;
			this.maxLeafElements = maxLeafElements;
			elementsCount = 0;

			if (maxDepth > 8)
			{
				// Currently no support for higher depths, the morton code lookup tables would have to support it
				throw new InvalidOperationException();
			}

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // Create the AtomicSafetyHandle and DisposeSentinel
            m_Safety = AtomicSafetyHandle.Create();

            // Set the safety ID on the AtomicSafetyHandle so that error messages describe this container type properly.
            AtomicSafetyHandle.SetStaticSafetyId(ref m_Safety, s_staticSafetyId);

            // Automatically bump the secondary version any time this container is scheduled for writing in a job
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(m_Safety, true);

            // Check if this is a nested container, and if so, set the nested container flag
            if (UnsafeUtility.IsNativeContainerType<T>())
                AtomicSafetyHandle.SetNestedContainer(m_Safety, true);
#endif

            // Allocate memory for every depth, the nodes on all depths are stored in a single continuous array
            var totalSize = DepthSizeLookup[maxDepth+1];
			lookup = UnsafeList<int>.Create(totalSize, allocator, NativeArrayOptions.ClearMemory);
			nodes = UnsafeList<QuadNode>.Create(totalSize, allocator, NativeArrayOptions.ClearMemory);
			elements = UnsafeList<QuadElement<T>>.Create(initialElementsCapacity, allocator);
		}

		public void ClearAndBulkInsert(NativeArray<QuadElement<T>> incomingElements)
		{
			// Always have to clear before bulk insert as otherwise the lookup and node allocations need to account
			// for existing data.
			Clear();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif

			int newCapacity = elements->Capacity;

			while (newCapacity < incomingElements.Length)
			{
				newCapacity <<= 1;
			}
			// Resize if needed
			if (newCapacity > elements->Capacity)
			{
				elements->Resize(newCapacity);
			}

			// Prepare morton codes
			var mortonCodes = new NativeArray<int>(incomingElements.Length, Allocator.Temp);
			var depthExtentsScaling = DepthLookup[maxDepth] / bounds.Extents;
			for (var i = 0; i < incomingElements.Length; i++)
			{
				var incPos = incomingElements[i].pos;
				incPos -= bounds.Center; // Offset by center
				incPos.y = -incPos.y; // World -> array
				var pos = (incPos + bounds.Extents) >> 1; // Make positive
				// Now scale into available space that belongs to the depth
				pos *= depthExtentsScaling;
				// And interleave the bits for the morton code
				// TODO: if element position is outside of quad tree bounds this will read outside the array
				mortonCodes[i] = (MortonLookup[pos.x] | (MortonLookup[pos.y] << 1));
			}

			// Index total child element count per node (total, so parent's counts include those of child nodes)
			for (var i = 0; i < mortonCodes.Length; i++)
			{
				int atIndex = 0;
				for (int depth = 0; depth <= maxDepth; depth++)
				{
					// Increment the node on this depth that this element is contained in
					(*(int*) ((IntPtr) lookup->Ptr + atIndex * sizeof (int)))++;
					atIndex = IncrementIndex(depth, mortonCodes, i, atIndex);
				}
			}

			// Prepare the tree leaf nodes
			RecursivePrepareLeaves(1, 1);

			// Add elements to leaf nodes
			for (var i = 0; i < incomingElements.Length; i++)
			{
				int atIndex = 0;

				for (int depth = 0; depth <= maxDepth; depth++)
				{
					var node = UnsafeUtility.ReadArrayElement<QuadNode>(nodes->Ptr, atIndex);
					
					if (node.leaf)
					{
						// We found a leaf, add this element to it and move to the next element
						UnsafeUtility.WriteArrayElement(elements->Ptr, node.index + node.count, incomingElements[i]);
						node.count++;
						UnsafeUtility.WriteArrayElement(nodes->Ptr, atIndex, node);
						break;
					}
					// No leaf found, we keep going deeper until we find one
					atIndex = IncrementIndex(depth, mortonCodes, i, atIndex);
				}
			}

			mortonCodes.Dispose();
		}

		int IncrementIndex(int depth, NativeArray<int> mortonCodes, int i, int atIndex)
		{
			var atDepth = math.max(0, maxDepth - depth);
			// Shift to the right and only get the first two bits
			int shiftedMortonCode = (mortonCodes[i] >> ((atDepth - 1) * 2)) & 0b11;
			// so the index becomes that... (0,1,2,3)
			atIndex += DepthSizeLookup[atDepth] * shiftedMortonCode;
			atIndex++; // offset for self
			return atIndex;
		}

		void RecursivePrepareLeaves(int prevOffset, int depth)
		{
			for (int l = 0; l < 4; l++)
			{
				var at = prevOffset + l * DepthSizeLookup[maxDepth - depth+1];

				var elementCount = UnsafeUtility.ReadArrayElement<int>(lookup->Ptr, at);

				if (elementCount > maxLeafElements && depth < maxDepth)
				{
					// There's more elements than allowed on this node so keep going deeper
					RecursivePrepareLeaves(at+1, depth+1);
				}
				else if (elementCount != 0)
				{
					// We either hit max depth or there's less than the max elements on this node, make it a leaf
					var node = new QuadNode { index = elementsCount, count = 0, leaf = true };
					UnsafeUtility.WriteArrayElement(nodes->Ptr, at, node);
					elementsCount += elementCount;
				}
			}
		}

		public void RangeQuery(AABB2D bounds, NativeList<QuadElement<T>> results)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
			new QuadTreeRangeQuery().Query(this, bounds, results);
		}

		public void Clear()
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
			UnsafeUtility.MemClear(lookup->Ptr, lookup->Capacity * UnsafeUtility.SizeOf<int>());
			UnsafeUtility.MemClear(nodes->Ptr, nodes->Capacity * UnsafeUtility.SizeOf<QuadNode>());
			UnsafeUtility.MemClear(elements->Ptr, elements->Capacity * UnsafeUtility.SizeOf<QuadElement<T>>());
			elementsCount = 0;
		}

		public void Dispose()
		{
            UnsafeList<QuadElement<T>>.Destroy(elements);
			elements = null;
			UnsafeList<int>.Destroy(lookup);
			lookup = null;
			UnsafeList<QuadNode>.Destroy(nodes);
			nodes = null;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
#endif
        }
	}
}
