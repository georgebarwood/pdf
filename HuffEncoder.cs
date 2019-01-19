using Generic = System.Collections.Generic;

namespace Pdf {

class HuffEncoder
{
  // Computes Huffman code lengths (nbits) and codes (tree_code) from frequencies.
  // Result is the number of codes, -1 indicates the bit limit was exceeded.  

  public static int ComputeCodes( int bitLimit, int [] freq, byte [] nbits, ushort [] tree_code )
  {
    int ncode = freq.Length;
    Heap<TreeNode> heap = new Heap<TreeNode>( ncode, TreeNode.LessThan );

    for ( int i = 0; i < ncode; i += 1 )
    {
      int f = freq[ i ];
      if ( f > 0 ) heap.Insert( new Leaf( (ushort)i, f ) );
    }

    // Assume nbits is already zeroed.
    // for ( int i = 0; i < nbits.Length; i += 1 ) nbits[ i ] = 0;

    if ( heap.Count == 1 )
    { 
      heap.Remove().GetBits( nbits, 1 );
    }
    else if ( heap.Count > 1 )
    {
      do // Keep pairing the lowest frequency TreeNodes.
      {
        heap.Insert( new Branch( heap.Remove(), heap.Remove() ) );
      }
      while ( heap.Count > 1 );
      heap.Remove().GetBits( nbits, 0 ); // Walk the tree to find the code lengths (nbits).
    }

    int maxBits = 0;
    for ( int i = 0; i < ncode; i += 1 ) 
      if ( nbits[ i ] > maxBits ) maxBits = nbits[ i ];

    if ( maxBits > bitLimit ) return -1;

    // Now compute codes, code below is from RFC 1951 page 7.

    int [] bl_count = new int[ maxBits+1 ];
    for ( int i = 0; i < ncode; i += 1 ) bl_count[ nbits[ i ] ] += 1;

    int [] next_code = new int[ maxBits+1 ];
    int code = 0; bl_count[ 0 ] = 0;
    for ( int i = 0; i < maxBits; i += 1 ) 
    {
      code = ( code + bl_count[ i ] ) << 1;
      next_code[ i+1 ] = code;
    }

    for ( int i = 0; i < ncode; i += 1 ) 
    {
      int length = nbits[ i ];
      if ( length != 0 ) 
      {
        tree_code[ i ] = (ushort)Reverse( next_code[ length ], length );
        next_code[ length ] += 1;
      }
    }
    while ( ncode > 0 && nbits[ ncode - 1 ] == 0 ) ncode -= 1;

    // System.Console.WriteLine( "HuffEncoder.ComputeCodes" );
    //    for ( int i = 0; i < ncode; i += 1 ) if ( nbits[ i ] > 0 )
    //      System.Console.WriteLine( "i=" + i + " len=" + nbits[ i ] + " tc=" + tree_code[ i ].ToString("X") + " freq=" + freq[ i ] );

    return ncode;
  }

  private static int Reverse( int x, int bits )
  // Reverse a string of bits ( ready to be output as Huffman code ).
  { 
    int result = 0; 
    for ( int i = 0; i < bits; i += 1 ) 
    {
      result <<= 1; 
      result |= x & 1; 
      x >>= 1; 
    } 
    return result; 
  } 

  private abstract class TreeNode
  { 
    public int Freq; 
    public ushort Code; 
    public byte Depth; 

    public static bool LessThan( TreeNode a, TreeNode b )
    { 
      return a.Freq < b.Freq || a.Freq == b.Freq && a.Depth < b.Depth;
    }

    public abstract void GetBits( byte [] nbits, int length );

  }

  private class Leaf : TreeNode
  {
    public Leaf( ushort code, int freq )
    {
      Code = code;
      Freq = freq;
    }

    public override void GetBits( byte [] nbits, int length )
    { 
      nbits[ Code ] = (byte)length;
    }
  } // end class Leaf

  private class Branch : TreeNode
  {
    TreeNode Left, Right; 

    public Branch( TreeNode left, TreeNode right )
    {
      Left = left;
      Right = right;
      Freq = left.Freq + right.Freq;
      Depth = (byte)( 1 + ( left.Depth > right.Depth ? left.Depth : right.Depth ) );
    }

    public override void GetBits( byte [] nbits, int length )
    { 
      Left.GetBits( nbits, length + 1 ); 
      Right.GetBits( nbits, length + 1 ); 
    }
  } // end class Branch

} // end class HuffEncoder

class Heap<T>
{
  public delegate bool DLessThan( T a, T b );

  public int Count { get{ return _Count; } }
  int _Count;
  T [] Array;
  DLessThan LessThan;

  public Heap ( int capacity, DLessThan lessThan )
  {
    Array = new T[ capacity ];
    LessThan = lessThan;
  }
  
  public void Insert( T n )
  {
    int j = _Count++;
    while ( j > 0 )
    {
      int p = ( j - 1 ) / 2; // Index of parent.
      T pn = Array[ p ];
      if ( !LessThan( n, pn ) ) break;
      Array[ j ] = pn; // Demote parent.
      j = p;
    }    
    Array[ j ] = n;
  }

  public T Remove() 
  {
    T result = Array[ 0 ];
    _Count -= 1;
    T n = Array[ _Count ];
    Array[ _Count ] = default(T);
    int j = 0;
    while ( true )
    {
      int c = j * 2 + 1; if ( c >= _Count ) break;
      T cn = Array[ c ];
      if ( c + 1 < _Count )
      {
        T cn2 = Array[ c + 1 ];
        if ( LessThan( cn2, cn ) ) { c += 1; cn = cn2; }
      } 
      if ( !LessThan( cn, n ) ) break;
      Array[ j ] = cn; j = c;  
    }
    Array[ j ] = n;
    return result;
  }

} // end class Heap

} // namespace
