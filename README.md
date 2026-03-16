# NativeQuadTree
Quad Tree for 2D spatial checks in Unity DOTS

HOW TO USE:
1) Allocate NativeQuadTree with max possible AABB2D bounds.
1) Prepare NativeQuadTree by adding elements via calling ClearAndBulkInsert(NativeArray<QuadElement<T>> incomingElements).
2) To get all elements within custom bounds call RangeQuery(AABB2D bounds, NativeList<QuadElement<T>> results);
