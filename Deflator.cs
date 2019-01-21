namespace Pdf {

/* RFC 1951 compression ( https://www.ietf.org/rfc/rfc1951.txt ) aims to compress a stream of bytes using :

   (1) LZ77 matching, where if an input sequences of at least 3 bytes re-occurs, it may be coded 
       as a <length,distance> pointer.

   (2) Huffman coding, where variable length codes are used, with more frequently used symbols encoded in less bits.

   The input may be split into blocks, a new block introduces a new set of Huffman codes. The choice of block 
   boundaries can affect compression. The method used to determine the block size is as follows:

   (1) The size of the next block to be output is set to an initial value.

   (2) A comparison is made between encoding two blocks of this size, or a double-length block.

   (3) If the double-length encoding is better, that becomes the block size, and the process repeats.

   LZ77 compression is implemented as suggested in the standard, although no attempt is made to
   truncate searches ( except searches terminate when the distance limit of 32k bytes is reached ).

   Only dynamic huffman blocks are used, no attempt is made to use Fixed or Copy blocks.

   Deflator ( this code) typically achieves better compression than ZLib ( http://www.componentace.com/zlib_.NET.htm 
   via https://zlib.net/, default settings ) by a few percent, and is faster on small inputs, but slower 
   on large inputs ( perhaps due to searches not being truncated ).

   For example, compressing a font file FreeSans.ttf ( 264,072 bytes ), Zlib output is 148,324 bytes
   in 44 milliseconds, whereas Deflator output is 144,289 bytes, 4,035 bytes smaller, in 59 milliseconds.

   Compressing a C# source file of 19,483 bytes, Zlib output size was 5,965 bytes in 27 milliseconds, 
   whereas Deflator output was 5,890 bytes, 75 bytes smaller, in 16 milliseconds.

   Sample usage:

   byte [] data = { 1, 2, 3, 4 };
   var mbs = new MemoryBitStream();
   Deflator.Deflate( data, mbs, 1 );
   byte [] deflated_data = mbs.ToArray();
  
   The MemoryBitStream may alternatively be copied to a stream, this may be useful when writing PDF files ( the intended use case ).

   Auxiliary top level classes/structs ( included in this file ): 
   *  OutBitStream.
   *  MemoryBitStream : an implementation of OutBitStream.
   *  HuffmanCoding calculates Huffman codes.
   *  Heap : used to implemnt HuffmanCoding.
*/   

sealed class Deflator 
{
  public static void Deflate( byte [] input, OutBitStream output, int format )
  {
    Deflator d = new Deflator( input, output );
    if ( format == 1 ) output.WriteBits( 16, 0x9c78 ); // RFC 1950 bytes.
    d.FindMatches( input );
    d.Buffered = input.Length;
    while ( !d.OutputBlock( true ) );
    if ( format == 1 )
    { 
      output.Pad( 8 );
      output.WriteBits( 32, Adler32( input ) ); // RFC 1950 checksum. 
    }  
  }

  // Private constants.

  // RFC 1951 limits.
  private const int MinMatch = 3;
  private const int MaxMatch = 258;
  private const int MaxDistance = 0x8000;

  private const int StartBlockSize = 0x1000; // Initial blocksize, actual may be larger or smaller. Need not be power of two.
  private const bool DynamicBlockSize = true; 
  private const int MaxBufferSize = 0x8000; // Must be power of 2.

  // Instead of initialising LZ77 hashTable and link arrays to -(MaxDistance+1), EncodePosition 
  // is added when storing a value and subtracted when retrieving a value.
  // This means a default value of 0 will always be more distant than MaxDistance.
  private const int EncodePosition = MaxDistance + 1;

  // Private fields.

  private byte [] Input;
  private OutBitStream Output;

  private int Buffered; // How many Input bytes have been processed to intermediate buffer.
  private int Finished; // How many Input bytes have been written to Output.

  // Intermediate circular buffer for storing LZ77 matches.
  private int    [] PositionBuffer;
  private ushort [] LengthBuffer;
  private ushort [] DistanceBuffer;
  private int BufferMask;
  private int BufferWrite, BufferRead; // Indexes for writing and reading.

  // Private functions and classes.

  private Deflator( byte [] input, OutBitStream output )
  { 
    Input = input; 
    Output = output; 
    
    int bufferSize = CalcBufferSize( input.Length / 3, MaxBufferSize );
    PositionBuffer = new int[ bufferSize ];
    LengthBuffer   = new ushort[ bufferSize ];
    DistanceBuffer = new ushort[ bufferSize ];   
    BufferMask = bufferSize - 1; 
  }

  public static int CalcBufferSize( int n, int max )
  // Calculates a power of 2 >= n, but not more than max.
  {
    if ( n >= max ) return max;
    int result = 1;
    while ( result < n ) result = result << 1;
    return result;
  }

  private void FindMatches( byte [] input ) // LZ77 compression.
  {
    if ( input.Length < MinMatch ) return;
    int limit = input.Length - 2;

    int hashShift = CalcHashShift( limit * 2 );
    uint hashMask = ( 1u << ( MinMatch * hashShift ) ) - 1;

    int [] hashTable = new int[ hashMask + 1 ];
    int [] link = new int[ limit ];

    int position = 0; // position in input.
    uint hash = ( (uint)input[ 0 ] << hashShift ) + input[ 1 ];

    while ( position < limit )
    {
      hash = ( ( hash << hashShift ) + input[ position + 2 ] ) & hashMask;        
      int hashEntry = hashTable[ hash ];
      hashTable[ hash ] = position + EncodePosition;
      if ( position >= hashEntry ) // Equivalent to position - ( hashEntry - EncodePosition ) > MaxDistance.
      {
         position += 1;
         continue;
      }
      link[ position ] = hashEntry;

      int distance, match = BestMatch( input, link, hashEntry - EncodePosition, position, out distance );
      position += 1;
      if ( match < MinMatch ) continue;

      // "Lazy matching" RFC 1951 p.15 : if there are overlapping matches, there is a choice over which of the match to use.
      // Example: "abc012bc345.... abc345". Here abc345 can be encoded as either [abc][345] or as a[bc345].
      // Since a range typically needs more bits to encode than a single literal, choose the latter.
      while ( position < limit ) 
      {
        hash = ( ( hash << hashShift ) + input[ position + 2 ] ) & hashMask;          
        hashEntry = hashTable[ hash ];
        hashTable[ hash ] = position + EncodePosition;
        if ( position >= hashEntry ) break;
        link[ position ] = hashEntry;

        int distance2, match2 = BestMatch( input, link, hashEntry - EncodePosition, position, out distance2 );
        if ( match2 > match || match2 == match && distance2 < distance )
        {
          match = match2;
          distance = distance2;
          position += 1;
        }
        else break;
      }

      int copyEnd = SaveMatch( position - 1, match, distance );
      if ( copyEnd > limit ) copyEnd = limit;

      position += 1;

      // Advance to end of copied section.
      while ( position < copyEnd )
      { 
        hash = ( ( hash << hashShift ) + input[ position + 2 ] ) & hashMask;
        link[ position ] = hashTable[ hash ];
        hashTable[ hash ] = position + EncodePosition;
        position += 1;
      }
    }
  }

  private static int BestMatch( byte [] input, int [] link, int oldPosition, int position, out int distance )
  { 
    int avail = input.Length - position;
    if ( avail > MaxMatch ) avail = MaxMatch;

    int bestMatch = 0, bestDistance = 0;

    while ( true )
    { 
      if ( input[ position + bestMatch ] == input[ oldPosition + bestMatch ] )
      {
        int match = MatchLength( input, position, oldPosition );
        if ( match > bestMatch )
        {
          bestMatch = match;
          bestDistance = position - oldPosition;
          if ( bestMatch == avail ) break;
        }
      }
      oldPosition = link[ oldPosition ];
      if ( position >= oldPosition ) break;
      oldPosition -= EncodePosition;
    }
    distance = bestDistance;
    return bestMatch;
  }

  private static int MatchLength( byte [] input, int p, int q )
  {
    int end = input.Length;
    if ( end > p + MaxMatch ) end = p + MaxMatch;
    int pstart = p;
    while ( p < end && input[ p ] == input [ q ] )
    {
      p += 1;
      q += 1;
    }
    return p - pstart;
  }

  private static int CalcHashShift( int n )
  {
    int p = 1;
    int result = 0;
    while ( n > p ) 
    {
      p = p << MinMatch;
      result += 1;
      if ( result == 6 ) break;
    }
    return result;
  } 

  private int SaveMatch ( int position, int length, int distance )
  // Called from FindMatches to save a <length,distance> match. Returns position + length.
  {
    // System.Console.WriteLine( "SaveMatch at " + position + " length=" + length + " distance=" + distance );
    int i = BufferWrite;
    PositionBuffer[ i ] = position;
    LengthBuffer[ i ] = (ushort) length;
    DistanceBuffer[ i ] = (ushort) distance;
    i = ( i + 1 ) & BufferMask;
    if ( i == BufferRead ) OutputBlock( false );
    BufferWrite = i;
    position += length;
    Buffered = position;
    return position;
  }

  private bool OutputBlock( bool last )
  {
    int blockSize = Buffered - Finished; // Uncompressed size in bytes.
    
    if ( blockSize > StartBlockSize ) 
    {
      blockSize = ( last && blockSize < StartBlockSize*2 ) ? blockSize >> 1 : StartBlockSize;
    }

    Block b;
    int bits; // Compressed size in bits.

    // While block construction fails, reduce blockSize.
    while ( true )
    {
      b = new Block( this, blockSize, null, out bits );
      if ( bits >= 0 ) break;
      blockSize -= blockSize / 3;
    }     

    // Investigate larger block size.
    while ( b.End < Buffered && DynamicBlockSize ) 
    {
      // b2 is a block which starts just after b.
      int bits2; Block b2 = new Block( this, blockSize, b, out bits2 );
      if ( bits2 < 0 ) break;

      // b3 is the block which encodes b and b2 together.
      int bits3; Block b3 = new Block( this, b2.End - b.Start, null, out bits3 );
      if ( bits3 < 0 ) break;

      if ( bits3 > bits + bits2 ) break;

      bits = bits3;
      b = b3;
      blockSize += blockSize; 
    }      

    // Output the block.
    if ( b.End < Buffered ) last = false;
    b.WriteBlock( this, last );  
    return last;
  }

  public static uint Adler32( byte [] b ) // Checksum function per RFC 1950.
  {
    uint s1 = 1, s2 = 0;
    for ( int i = 0; i < b.Length; i += 1 )
    {
      s1 = ( s1 + b[ i ] ) % 65521;
      s2 = ( s2 + s1 ) % 65521;
    }
    return s2 * 65536 + s1;    
  }

  private class Block
  {
    public readonly int Start, End; // Range of input encoded.

    public Block( Deflator d, int blockSize, Block previous, out int bits )
    // The block is not immediately output, to allow caller to try different block sizes.
    // Instead, the number of bits neeed to encoded the block is returned ( excluding "extra" bits ).
    {
      Output = d.Output;
      bits = -1;

      if ( previous == null )
      {
        Start = d.Finished;
        BufferStart = d.BufferRead;
      }
      else
      {
        Start = previous.End;
        BufferStart = previous.BufferEnd;
      }

      int avail = d.Buffered - Start;
      if ( blockSize > avail ) blockSize = avail;

      End = TallyFrequencies( d, blockSize );
      Lit.Used[ 256 ] += 1; // End of block code.
     
      if ( Lit.ComputeCodes() || Dist.ComputeCodes() ) return;

      if ( Dist.Count == 0 ) Dist.Count = 1;

      // Compute length encoding.
      DoLengthPass( 1 );
      if ( Len.ComputeCodes() ) return;

      // The length codes are permuted before being stored ( so that # of trailing zeroes is likely to be more ).
      Len.Count = 19; while ( Len.Count > 4 && Len.Bits[ ClenAlphabet[ Len.Count - 1 ] ] == 0 ) Len.Count -= 1;

      bits = 17 + 3 * Len.Count + Len.Total() + Lit.Total() + Dist.Total();
    }

    public void WriteBlock( Deflator d, bool last )
    {
      OutBitStream output = Output;
      output.WriteBits( 1, last ? 1u : 0u );
      output.WriteBits( 2, 2 );
      output.WriteBits( 5, (uint)( Lit.Count - 257 ) ); 
      output.WriteBits( 5, (uint)( Dist.Count - 1 ) ); 
      output.WriteBits( 4, (uint)( Len.Count - 4 ) );

      for ( int i = 0; i < Len.Count; i += 1 ) 
        output.WriteBits( 3, Len.Bits[ ClenAlphabet[ i ] ] );

      DoLengthPass( 2 );
      PutCodes( d );
      output.WriteBits( Lit.Bits[ 256 ], Lit.Codes[ 256 ] ); // End of block code
    }

    // Block private fields and constants.

    private OutBitStream Output;
    private int BufferStart, BufferEnd;

    // Huffman codings : Lit = Literal or Match Code, Dist = Distance code, Len = Length code.
    HuffmanCoding Lit = new HuffmanCoding(15,288), Dist = new HuffmanCoding(15,32), Len = new HuffmanCoding(7,19);

    // Counts for code length encoding.
    private int LengthPass, PreviousLength, ZeroRun, Repeat;

    // RFC 1951 constants.
    private readonly static byte [] ClenAlphabet = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
    private readonly static byte [] MatchExtra = { 0,0,0,0, 0,0,0,0, 1,1,1,1, 2,2,2,2, 3,3,3,3, 4,4,4,4, 5,5,5,5, 0 };
    private readonly static ushort [] MatchOff = { 3,4,5,6, 7,8,9,10, 11,13,15,17, 19,23,27,31, 35,43,51,59, 67,83,99,115, 
      131,163,195,227, 258, 0xffff };
    private readonly static byte [] DistExtra = { 0,0,0,0, 1,1,2,2, 3,3,4,4, 5,5,6,6, 7,7,8,8, 9,9,10,10, 11,11,12,12, 13,13 };
    private readonly static ushort [] DistOff = { 1,2,3,4, 5,7,9,13, 17,25,33,49, 65,97,129,193, 257,385,513,769, 
      1025,1537,2049,3073, 4097,6145,8193,12289, 16385,24577, 0xffff };

    // Block private functions.

    private int TallyFrequencies( Deflator d, int blockSize )
    {
      int position = Start;
      int end = position + blockSize;

      int bufferRead = BufferStart;
      while ( position < end && bufferRead != d.BufferWrite )
      {
        int matchPosition = d.PositionBuffer[ bufferRead ];
        if ( matchPosition >= end ) break;

        int length = d.LengthBuffer[ bufferRead ];
        int distance = d.DistanceBuffer[ bufferRead ];
        bufferRead = ( bufferRead + 1 ) & d.BufferMask;

        byte [] input = d.Input;
        while ( position < matchPosition ) 
        {
          Lit.Used[ input[ position ] ] += 1;
          position += 1;
        }

        position += length;

        // Compute match and distance codes.
        int mc = 0; while ( length >= MatchOff[ mc ] ) mc += 1; mc -= 1;
        int dc = 29; while ( distance < DistOff[ dc ] ) dc -= 1;

        Lit.Used[ 257 + mc ] += 1;
        Dist.Used[ dc ] += 1;     
      }

      while ( position < end ) 
      {
        Lit.Used[ d.Input[ position ] ] += 1;
        position += 1;
      }
 
      BufferEnd = bufferRead;
      return position;
    }

    private void PutCodes( Deflator d )
    {
      byte [] input = d.Input;
      OutBitStream output = d.Output;

      int position = Start;
      int bufferRead = BufferStart;

      while ( position < End && bufferRead != d.BufferWrite )
      {
        int matchPosition = d.PositionBuffer[ bufferRead ];

        if ( matchPosition >= End ) break;

        int length = d.LengthBuffer[ bufferRead ];
        int distance = d.DistanceBuffer[ bufferRead  ]; 

        bufferRead = ( bufferRead  + 1 ) & d.BufferMask;

        while ( position < matchPosition ) 
        {
          byte b = d.Input[ position ];
          output.WriteBits( Lit.Bits[ b ], Lit.Codes[ b ] );
          position += 1;
        }  
        position += length;

        // Compute match and distance codes.
        int mc = 0; while ( length >= MatchOff[ mc ] ) mc += 1; mc -= 1;
        int dc = 29; while ( distance < DistOff[ dc ] ) dc -= 1;

        output.WriteBits( Lit.Bits[ 257 + mc ], Lit.Codes[ 257 + mc ] );
        output.WriteBits( MatchExtra[ mc ], (uint)(length-MatchOff[ mc ] ) );
        output.WriteBits( Dist.Bits[ dc ], Dist.Codes[ dc ] );
        output.WriteBits( DistExtra[ dc ], (uint)(distance-DistOff[ dc ] ) );    
      }

      while ( position < End ) 
      {
        byte b = input[ position ];
        output.WriteBits( Lit.Bits[ b ], Lit.Codes[ b ] );
        position += 1;
      }  

      d.BufferRead = bufferRead;
      d.Finished = position;
    }

    // Run length encoding of code lengths - RFC 1951, page 13.

    private void DoLengthPass( int pass )
    {
      LengthPass = pass; 
      EncodeLengths( Lit.Count, Lit.Bits, true );     
      EncodeLengths( Dist.Count, Dist.Bits, false );
    }

    private void PutLength( int code ) 
    { 
      if ( LengthPass == 1 ) 
        Len.Used[ code ] += 1; 
      else 
        Output.WriteBits( Len.Bits[ code ], Len.Codes[ code ] ); 
    }

    private void EncodeLengths( int n, byte [] lengths, bool isLit )
    {
      if ( isLit ) 
      { 
        PreviousLength = 0; 
        ZeroRun = 0; 
        Repeat = 0; 
      }
      for ( int i = 0; i < n; i += 1 )
      {
        int length = lengths[ i ];
        if ( length == 0 )
        { 
          EncodeRepeat(); 
          ZeroRun += 1; 
          PreviousLength = 0; 
        }
        else if ( length == PreviousLength ) 
        {
          Repeat += 1;
        }
        else 
        { 
          EncodeZeroRun(); 
          EncodeRepeat(); 
          PutLength( length ); 
          PreviousLength = length; 
        }
      }      
      if ( !isLit ) 
      { 
        EncodeZeroRun(); 
        EncodeRepeat();
      }
    }

    private void EncodeRepeat()
    {
      while ( Repeat > 0 )
      {
        if ( Repeat < 3 ) 
        { 
          PutLength( PreviousLength ); 
          Repeat -= 1; 
        }
        else 
        { 
          int x = Repeat; 
          if ( x > 6 ) x = 6; 
          PutLength( 16 ); 
          if ( LengthPass == 2 )
          { 
            Output.WriteBits( 2, (uint)( x - 3 ) ); 
          }
          Repeat -= x;  
        }
      }
    }

    private void EncodeZeroRun()
    {
      while ( ZeroRun > 0 )
      {
        if ( ZeroRun < 3 ) 
        { 
          PutLength( 0 ); 
          ZeroRun -= 1; 
        }
        else if ( ZeroRun < 11 ) 
        { 
          PutLength( 17 ); 
          if ( LengthPass == 2 ) Output.WriteBits( 3, (uint)( ZeroRun - 3 ) ); 
          ZeroRun = 0;  
        }
        else 
        { 
          int x = ZeroRun; 
          if ( x > 138 ) x = 138; 
          PutLength( 18 ); 
          if ( LengthPass == 2 ) Output.WriteBits( 7, (uint)( x - 11 ) ); 
          ZeroRun -= x; 
        }
      }
    }
  } // end class Block

} // end class Deflator


// ******************************************************************************

struct HuffmanCoding // Variable length coding.
{
  public int Count; // Number of used symbols.
  public int [] Used; // Count of how many times a symbol is used in the block being encoded.
  public byte [] Bits; // Number of bits used to encode a symbol.
  public ushort [] Codes; // Huffman code for a symbol ( bit 0 is most significant ).
  private int Limit; // Maxiumum number of bits for a code.

  public HuffmanCoding( int limit, int symbols )
  {
    Limit = limit;
    Count = symbols;
    Used = new int[ symbols ];
    Bits = new byte[ symbols ];
    Codes = new ushort[ symbols ];
  }

  public int Total()
  {
    int result = 0, count = Count;
    for ( int i = 0; i < count; i += 1 ) 
      result += Used[i] * Bits[i];
    return result;
  }

  public bool ComputeCodes() // returns true if Limit is exceeded.
  {
    int count = Count;

    Heap<TreeNode> heap = new Heap<TreeNode>( count, TreeNode.LessThan );

    for ( int i = 0; i < Count; i += 1 )
    {
      int used = Used[ i ];
      if ( used > 0 ) heap.Insert( new Leaf( (ushort)i, used ) );
    }

    int maxBits = 0;

    if ( heap.Count == 1 )
    { 
      heap.Remove().GetBits( Bits, 1 );
      maxBits = 1;
    }
    else if ( heap.Count > 1 )
    {
      do // Keep pairing the lowest frequency TreeNodes.
      {
        heap.Insert( new Branch( heap.Remove(), heap.Remove() ) );
      }  while ( heap.Count > 1 );

      TreeNode root = heap.Remove();
      maxBits = root.Depth;
      if ( maxBits > Limit ) return true;
      root.GetBits( Bits, 0 ); // Walk the tree to find the code lengths (Bits).
    }

    // Compute codes, code below is from RFC 1951 page 7.

    int [] bl_count = new int[ maxBits + 1 ];
    for ( int i = 0; i < count; i += 1 ) bl_count[ Bits[ i ] ] += 1;

    int [] next_code = new int[ maxBits + 1 ];
    int code = 0; bl_count[ 0 ] = 0;
    for ( int i = 0; i < maxBits; i += 1 ) 
    {
      code = ( code + bl_count[ i ] ) << 1;
      next_code[ i+1 ] = code;
    }

    for ( int i = 0; i < count; i += 1 ) 
    {
      int length = Bits[ i ];
      if ( length != 0 ) 
      {
        Codes[ i ] = (ushort)Reverse( next_code[ length ], length );
        next_code[ length ] += 1;
      }
    }

    // Reduce count if there are unused symbols.
    while ( count > 0 && Bits[ count - 1 ] == 0 ) count -= 1;
    Count = count;

    //System.Console.WriteLine( "HuffEncoder.ComputeCodes" );
    //    for ( int i = 0; i < count; i += 1 ) if ( Bits[ i ] > 0 )
    //      System.Console.WriteLine( "i=" + i + " len=" + Bits[ i ] + " tc=" + Codes[ i ].ToString("X") + " freq=" + Used[ i ] );

    return false;
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
    public int Used; 
    public byte Depth; 

    public static bool LessThan( TreeNode a, TreeNode b )
    { 
      return a.Used < b.Used || a.Used == b.Used && a.Depth < b.Depth;
    }

    public abstract void GetBits( byte [] nbits, int length );

  }

  private class Leaf : TreeNode
  {
    public ushort Code; 

    public Leaf( ushort code, int used )
    {
      Code = code;
      Used = used;
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
      Used = left.Used + right.Used;
      Depth = (byte)( 1 + ( left.Depth > right.Depth ? left.Depth : right.Depth ) );
    }

    public override void GetBits( byte [] nbits, int length )
    { 
      Left.GetBits( nbits, length + 1 ); 
      Right.GetBits( nbits, length + 1 ); 
    }
  } // end class Branch

} // end struct HuffmanCoding


// ******************************************************************************


sealed class Heap<T> // An array organised so the smallest element can be efficiently removed.
{
  public delegate bool DLessThan( T a, T b );

  public int Count { get{ return _Count; } }
  private int _Count;
  private T [] Array;
  private DLessThan LessThan;

  public Heap ( int capacity, DLessThan lessThan )
  {
    Array = new T[ capacity ];
    LessThan = lessThan;
  }
  
  public void Insert( T e )
  {
    int j = _Count++;
    while ( j > 0 )
    {
      int p = ( j - 1 ) / 2; // Index of parent.
      T pe = Array[ p ];
      if ( !LessThan( e, pe ) ) break;
      Array[ j ] = pe; // Demote parent.
      j = p;
    }    
    Array[ j ] = e;
  }

  public T Remove() // Returns the smallest element.
  {
    T result = Array[ 0 ];
    _Count -= 1;
    T e = Array[ _Count ];
    Array[ _Count ] = default(T);
    int j = 0;
    while ( true )
    {
      int c = j * 2 + 1; if ( c >= _Count ) break;
      T ce = Array[ c ];
      if ( c + 1 < _Count )
      {
        T ce2 = Array[ c + 1 ];
        if ( LessThan( ce2, ce ) ) { c += 1; ce = ce2; }
      } 
      if ( !LessThan( ce, e ) ) break;
      Array[ j ] = ce; j = c;  
    }
    Array[ j ] = e;
    return result;
  }

} // end class Heap


// ******************************************************************************


abstract class OutBitStream
{
  public void WriteBits( int n, ulong value )
  // Write first n ( 0 <= n <= 64 ) bits of value to stream, least significant bit is written first.
  // Unused bits of value must be zero, i.e. value must be in range 0 .. 2^n-1.
  {
    if ( n + BitsInWord >= WordCapacity )
    {
      Save( Word | value << BitsInWord );
      int space = WordCapacity - BitsInWord;
      value >>= space;
      n -= space;
      Word = 0;
      BitsInWord = 0;
    }
    Word |= value << BitsInWord;
    BitsInWord += n;
  }

  public void Pad( int n )
  // Pad with zero bits to n bit boundary where n is power of 2 in range 1,2,4..64, typically n=8.
  {
    int w = BitsInWord % n; 
    if ( w > 0 ) WriteBits( n - w, 0 );
  }

  public abstract void Save( ulong word );

  protected const int WordSize = sizeof(ulong);  // Size of Word in bytes.
  protected const int WordCapacity = WordSize * 8; // Number of bits that can be stored Word

  protected ulong Word; // Bits are first stored in Word, when full, Word is saved.
  protected int BitsInWord; // Number of bits currently stored in Word.
}


// ******************************************************************************


sealed class MemoryBitStream : OutBitStream
{
  // ByteSize returns the current size in bytes.
  // CopyTo copies the contents to a Stream.
  // ToArray returns the contents as an array of bytes.

  public int ByteSize() 
  {
    return ( CompleteChunks * Chunk.Capacity + WordsInCurrentChunk ) * WordSize + ( BitsInWord + 7 ) / 8;
  }

  public void CopyTo( System.IO.Stream s ) 
  {
    byte [] buffer = new byte [ WordSize ];
    for ( Chunk c = FirstChunk; c != null; c = c.Next )
    { 
      int n = ( c == CurrentChunk ) ? WordsInCurrentChunk : Chunk.Capacity;
      for ( int j = 0; j < n; j += 1 ) 
      {
        ulong w = c.Words[ j ];
        unchecked
        {
          buffer[0] = (byte) w;
          buffer[1] = (byte)( w >> 8 );
          buffer[2] = (byte)( w >> 16 );
          buffer[3] = (byte)( w >> 24 );
          buffer[4] = (byte)( w >> 32 );
          buffer[5] = (byte)( w >> 40 );
          buffer[6] = (byte)( w >> 48 );
          buffer[7] = (byte)( w >> 56 );
        }
        s.Write( buffer, 0, 8 ); 
      }
    }

    int biw = BitsInWord;
    ulong word = Word;
    while ( biw > 0 )
    {
      s.WriteByte( unchecked( (byte) word ) );
      word >>= 8;
      biw -= 8;
    }
  }

  public byte [] ToArray()
  {
    byte [] buffer = new byte[ ByteSize() ];
    int i = 0;

    for ( Chunk c = FirstChunk; c != null; c = c.Next )
    { 
      int n = ( c == CurrentChunk ) ? WordsInCurrentChunk : Chunk.Capacity;
      for ( int j = 0; j < n; j += 1 ) 
      {
        ulong w = c.Words[ j ];
        unchecked
        {
          buffer[i++] = (byte) w;
          buffer[i++] = (byte)( w >> 8 );
          buffer[i++] = (byte)( w >> 16 );
          buffer[i++] = (byte)( w >> 24 );
          buffer[i++] = (byte)( w >> 32 );
          buffer[i++] = (byte)( w >> 40 );
          buffer[i++] = (byte)( w >> 48 );
          buffer[i++] = (byte)( w >> 56 );
        }
      }
    }

    int biw = BitsInWord;
    ulong word = Word;
    while ( biw > 0 )
    {
      buffer[ i++ ] = unchecked( (byte) word );
      word >>= 8;
      biw -= 8;
    }
    return buffer;
  }

  public MemoryBitStream()
  {
    FirstChunk = new Chunk();
    CurrentChunk = FirstChunk;
  }

  public override void Save( ulong word )
  {
    if ( WordsInCurrentChunk == Chunk.Capacity )
    {
      Chunk nc = new Chunk();
      CurrentChunk.Next = nc;
      CurrentChunk = nc;
      CompleteChunks += 1;
      WordsInCurrentChunk = 0;
    }
    CurrentChunk.Words[ WordsInCurrentChunk++ ] = word;
  }

  private int WordsInCurrentChunk; // Number of words stored in CurrentChunk.
  private int CompleteChunks; // Number of complete Chunks.
  private Chunk FirstChunk, CurrentChunk;

  private class Chunk
  {
    public const int Capacity = 256;
    public ulong [] Words = new ulong[ Capacity ];
    public Chunk Next;
  }

} // end class MemoryBitStream

} // namespace