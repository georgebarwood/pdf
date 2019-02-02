using Generic = System.Collections.Generic;
using Monitor = System.Threading.Monitor;
using ThreadPool = System.Threading.ThreadPool;
using Thread = System.Threading.Thread;

#if x32
using uword = System.UInt32;
#else
using uword = System.UInt64;
#endif

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

   A second thread is used to perform the LZ77 compression, to allow the Huffman coding and LZ77 
   compression to be done in parallel.

   Deflator ( this code) typically achieves better compression than ZLib 
   ( http://www.componentace.com/zlib_.NET.htm  via https://zlib.net/ ) 
   while compressing at a similar speed ( default options, after warmup ).

   For example, compressing a font file FreeSans.ttf ( 264,072 bytes ), Zlib output 
   is 148,324 bytes in 19 milliseconds, whereas Deflator output is 143,666 bytes 
   in the same time.

   Sample usage:

   byte [] data = { 1, 2, 3, 4 };
   var mbs = new MemoryBitStream();
   Deflator.Deflate( data, mbs );
   byte [] deflated_data = mbs.ToArray();
  
   The MemoryBitStream may alternatively be copied to a stream, this may be useful when writing PDF files ( the intended use case ).

   Auxiliary top level classes/structs ( included in this file ): 
   *  OutBitStream.
   *  MemoryBitStream : an implementation of OutBitStream.
   *  HuffmanCoding calculates Huffman codes.
   *  UlongHeap : used to implement HuffmanCoding.
*/   

sealed class Deflator 
{

  public static void Deflate( byte [] input, OutBitStream output )  // Deflate with default options.
  {
    Deflator d = new Deflator( input, output );
    d.Go();
  }

  // Options : to amend these use new Deflator( input, output ) and set before calling Go().
  // Possible "fast" setting : StartBlockSize = 0x4000, LazyMatch = false, DynamicBlockSize = false.
  public int StartBlockSize = 0x1000; // Increase to go faster ( with less compression ), reduce to try for more compression.
  public int MaxBufferSize = 0x8000; // Must be power of 2.

  // Compression options - set false to go faster, with less compression.
  public bool LZ77 = true;
  public bool LazyMatch = true;
  public bool DynamicBlockSize = true; 
  public bool TuneBlockSize = true;

  public bool RFC1950 = true; // Set false to suppress RFC 1950 fields.

  public Deflator( byte [] input, OutBitStream output )
  { 
    Input = input; 
    Output = output; 
  }

  public void Go()
  {
    if ( RFC1950 ) 
    {
      Output.WriteBits( 16, 0x9c78 );
    }

    if ( LZ77 && Input.Length > MinMatch )
    {
      ThreadPool.QueueUserWorkItem( FindMatchesStart, this );
    }
    else
    {
      Buffered = Input.Length;
    }

    bool lastBlock;
    do
    {
      Block b = GetBlock();
      lastBlock = b.End == Input.Length;
      b.WriteBlock( this, lastBlock );  
    } while ( !lastBlock );

    if ( RFC1950 )
    { 
      Output.Pad( 8 );
      Output.WriteBits( 32, Adler32( Input ) );
    } 
  }

  // Private constants.

  // RFC 1951 match ( LZ77 ) limits.
  private const int MinMatch = 3; // The smallest match eligible for LZ77 encoding.
  private const int MaxMatch = 258; // The largest match eligible for LZ77 encoding.
  private const int MaxDistance = 0x8000; // The largest distance backwards in input from current position that can be encoded.

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
  private byte   [] LengthBuffer;
  private ushort [] DistanceBuffer;
  private int BufferMask;
  private int BufferWrite, BufferRead; // Indexes for writing and reading.

  // Inter-thread signalling fields.
  private readonly System.Object BufferLock = new System.Object();
  private bool BufferFull = false;
  private bool InputWait = false;
  private int InputRequest;

  // LZ77 hash table ( for MatchPossible function ).
  private int HashShift;
  private uint HashMask;
  private int [] HashTable;

  // Private functions and classes.

  private static void FindMatchesStart( System.Object x )
  {
    Deflator d = (Deflator) x;
    d.FindMatches();
    lock( d.BufferLock )
    {
      d.Buffered = d.Input.Length;
      if ( d.InputWait ) Monitor.Pulse( d.BufferLock );
    } 
  }

  private void FindMatches() // LZ77 compression.
  {
    byte [] input = Input;

    int bufferSize = CalcBufferSize( input.Length / 3, MaxBufferSize );
    PositionBuffer = new int[ bufferSize ];
    LengthBuffer   = new byte[ bufferSize ];
    DistanceBuffer = new ushort[ bufferSize ];   
    BufferMask = bufferSize - 1; 

    int limit = input.Length - 2;

    int hashShift = CalcHashShift( limit * 2 );
    uint hashMask = ( 1u << ( MinMatch * hashShift ) ) - 1;

    int [] hashTable = new int[ hashMask + 1 ];
    int [] link = new int[ limit ];

    HashShift = hashShift;
    HashMask = hashMask;
    HashTable = hashTable;

    int position = 0; // position in input.

    // hash will be hash of three bytes starting at position.
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

      int distance, match = BestMatch( input, position, out distance, hashEntry - EncodePosition, link );
      position += 1;
      if ( match < MinMatch ) continue;

      // "Lazy matching" RFC 1951 p.15 : if there are overlapping matches, there is a choice over which of the match to use.
      // Example: "abc012bc345.... abc345". Here abc345 can be encoded as either [abc][345] or as a[bc345].
      // Since a range typically needs more bits to encode than a single literal, choose the latter.
      while ( LazyMatch && position < limit ) 
      {
        hash = ( ( hash << hashShift ) + input[ position + 2 ] ) & hashMask;          
        hashEntry = hashTable[ hash ];
        hashTable[ hash ] = position + EncodePosition;
        if ( position >= hashEntry ) break;
        link[ position ] = hashEntry;

        int distance2, match2 = BestMatch( input, position, out distance2, hashEntry - EncodePosition, link );
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

    HashTable = null; // To potentially free up memory.

  }

  // BestMatch finds the best match starting at position. 
  // oldPosition is from hash table, link [] is linked list of older positions.

  private int BestMatch( byte [] input, int position, out int bestDistance, int oldPosition, int [] link )
  { 
    int avail = input.Length - position;
    if ( avail > MaxMatch ) avail = MaxMatch;

    int bestMatch = 0; bestDistance = 0;
    byte keyByte = input[ position + bestMatch ];

    while ( true )
    { 
      if ( input[ oldPosition + bestMatch ] == keyByte )
      {
        int match = 0; 
        while ( match < avail && input[ position + match ] == input[ oldPosition + match ] ) 
        {
          match += 1;
        }
        if ( match > bestMatch )
        {
          bestMatch = match;
          bestDistance = position - oldPosition;
          if ( bestMatch == avail || ! MatchPossible( position, bestMatch ) ) break;
          keyByte = input[ position + bestMatch ];
        }
      }
      oldPosition = link[ oldPosition ];
      if ( position >= oldPosition ) break;
      oldPosition -= EncodePosition;
    }
    return bestMatch;
  }

  // MatchPossible is used to try and shorten the BestMatch search by checking whether 
  // there is a hash entry for the last 3 bytes of the next longest possible match.

  private bool MatchPossible( int position, int bestMatch )
  {
    position += bestMatch - 2;
    uint hash = ( (uint)Input[ position ] << HashShift ) + Input[ position + 1 ];
    hash = ( ( hash << HashShift ) + Input[ position + 2 ] ) & HashMask;        
    return position < HashTable[ hash ];
  }

  private int SaveMatch ( int position, int length, int distance )
  // Called from FindMatches to save a <length,distance> match. Returns position + length.
  {
    { for ( int j=0; j<length; j+=1 ) if ( Input[position+j] != Input[position-distance+j] ) throw new System.Exception(); }


    int i = BufferWrite;
    PositionBuffer[ i ] = position;
    LengthBuffer[ i ] = (byte) ( length - MinMatch );
    DistanceBuffer[ i ] = (ushort) distance;
    i = ( i + 1 ) & BufferMask;

    while ( i == BufferRead )
    {
      lock ( BufferLock )
      {
        if ( i == BufferRead ) 
        {
          BufferFull = true;
          if ( InputWait ) Monitor.Pulse( BufferLock );
          Monitor.Wait( BufferLock );
          BufferFull = false;
        }
      }
    }

    Thread.MemoryBarrier();
    BufferWrite = i;
    position += length;
    Thread.MemoryBarrier();
    Buffered = position;

    if ( InputWait && position >= InputRequest )
      lock ( BufferLock ) Monitor.Pulse( BufferLock );

    return position;
  }

  private void BlockWritten( int bufferEnd, int end )
  {
    Thread.MemoryBarrier();
    BufferRead = bufferEnd;
    Finished = end;
    if ( LZ77 ) lock( BufferLock ) 
    { 
      if ( BufferFull ) Monitor.Pulse( BufferLock );
    }
  }

  private int WaitForInput( int request )
  {
    while ( true ) 
    {
      lock( BufferLock )
      {
        if ( Buffered == Input.Length || BufferFull || Buffered >= request ) 
          return Buffered;
        else
        {
          InputRequest = request;
          InputWait = true;
          Monitor.Wait( BufferLock );
          InputWait = false;
        }
      }
    }
  }

  private Block GetBlock()
  {
    int blockSize = WaitForInput( Finished + StartBlockSize ) - Finished;
  
    if ( blockSize > StartBlockSize ) 
    {
      blockSize = ( Buffered == Input.Length && blockSize <= StartBlockSize * 2 ) ? blockSize  : StartBlockSize;
    }

    Block b = new Block( this, blockSize, null );
    int bits = b.GetBits(); // Compressed size in bits.
    int tunedBlockSize = blockSize;

    // Investigate larger block size.
    while ( DynamicBlockSize ) 
    {
      int avail = WaitForInput( b.End + blockSize );

      if ( avail < b.End + blockSize ) break;

      if ( blockSize == 0 ) break;       

      // b2 is a block which starts just after b.
      Block b2 = new Block( this, blockSize, b );

      // b3 covers b and b2 exactly as one block.
      Block b3 = new Block( this, b2.End - b.Start, null );
      
      int bits2 = b2.GetBits();
      int bits3 = b3.GetBits(); 

      int delta = TuneBlockSize ? b2.TuneBoundary( this, b, blockSize / 4, out tunedBlockSize ) : 0;

      if ( bits3 > bits + bits2 + delta ) break;

      bits = bits3;
      b = b3;
      blockSize += blockSize; 
      tunedBlockSize = blockSize;
    }      

    if ( tunedBlockSize > blockSize )
    {
      b = new Block( this, tunedBlockSize, null ); 
      b.GetBits();
    }

    return b;
  }

  private static int CalcBufferSize( int n, int max )
  // Calculates a power of 2 >= n, but not more than max.
  {
    if ( n >= max ) return max;
    int result = 1;
    while ( result < n ) result = result << 1;
    return result;
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

    public Block( Deflator d, int blockSize, Block previous )
    // The block is not immediately output, to allow caller to try different block sizes.
    // Instead, the number of bits neeed to encoded the block is returned by GetBits ( excluding "extra" bits ).
    {
      Output = d.Output;

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

      if ( Start + blockSize > d.Buffered ) throw new System.Exception();

      End = TallyFrequencies( d, blockSize, out BufferEnd );
      Lit.Used[ 256 ] += 1; // End of block code.
    }

    public int GetBits()
    {
      Lit.ComputeCodes();
      Dist.ComputeCodes();

      if ( Dist.Count == 0 ) Dist.Count = 1;

      // Compute length encoding.
      DoLengthPass( 1 );
      Len.ComputeCodes();

      // The length codes are permuted before being stored ( so that # of trailing zeroes is likely to be more ).
      Len.Count = 19; while ( Len.Count > 4 && Len.Bits[ ClenAlphabet[ Len.Count - 1 ] ] == 0 ) Len.Count -= 1;

      return 17 + 3 * Len.Count + Len.Total() + Lit.Total() + Dist.Total();
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

      d.BlockWritten( BufferEnd, End );
    }

    // Block private fields and constants.

    private readonly OutBitStream Output;
    private readonly int BufferStart, BufferEnd;

    // Huffman codings : Lit = Literal or Match Code, Dist = Distance code, Len = Length code.
    private HuffmanCoding 
      Lit = new HuffmanCoding( 15, 288 ), 
      Dist = new HuffmanCoding( 15, 32 ), 
      Len = new HuffmanCoding( 7, 19 );

    // Counts for code length encoding.
    private int LengthPass, PreviousLength, ZeroRun, Repeat;

    // RFC 1951 constants.
    private readonly static byte [] ClenAlphabet = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
    private readonly static byte [] MatchExtra = { 0,0,0,0, 0,0,0,0, 1,1,1,1, 2,2,2,2, 3,3,3,3, 4,4,4,4, 5,5,5,5, 0 };
    private readonly static ushort [] MatchOff = { 3,4,5,6, 7,8,9,10, 11,13,15,17, 19,23,27,31, 35,43,51,59, 
      67,83,99,115,  131,163,195,227, 258, 0xffff };
    private readonly static byte [] DistExtra = { 0,0,0,0, 1,1,2,2, 3,3,4,4, 5,5,6,6, 7,7,8,8, 9,9,10,10, 11,11,12,12, 13,13 };
    private readonly static ushort [] DistOff = { 1,2,3,4, 5,7,9,13, 17,25,33,49, 65,97,129,193, 257,385,513,769, 
      1025,1537,2049,3073, 4097,6145,8193,12289, 16385,24577 };

    // Block private functions.

    private int TallyFrequencies( Deflator d, int blockSize, out int bufferRead )
    {
      // Counts how many times each symbol is used, also determines exact end of block.

      int position = Start;
      int end = position + blockSize;

      bufferRead = BufferStart;
      while ( position < end && bufferRead != d.BufferWrite )
      {
        int matchPosition = d.PositionBuffer[ bufferRead ];
        if ( matchPosition >= end ) break;

        int length = d.LengthBuffer[ bufferRead ] + MinMatch;
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
 
      return position;
    }

    public int TuneBoundary( Deflator d, Block prev, int howfar, out int blockSize )
    {
      // Investigate whether moving data into the previous block uses fewer bits,
      // using the current encodings. If a symbol with no encoding in the 
      // previous block is found, terminate the search ( goto EndSearch ).

      int position = Start;
      int bufferRead = BufferStart;
      int end = position + howfar;
      if ( end > End ) end = End;

      int delta = 0, bestDelta = 0, bestPosition = position;

      while ( bufferRead != BufferEnd )
      {
        int matchPosition = d.PositionBuffer[ bufferRead ];

        int length = d.LengthBuffer[ bufferRead ] + MinMatch;
        int distance = d.DistanceBuffer[ bufferRead  ]; 

        bufferRead = ( bufferRead  + 1 ) & d.BufferMask;

        while ( position < matchPosition ) 
        {
          byte b = d.Input[ position ];
 
          if ( prev.Lit.Bits[ b ] == 0 ) goto EndSearch;
          delta += prev.Lit.Bits[ b ] - Lit.Bits[ b ];
          if ( delta < bestDelta ) { bestDelta = delta; bestPosition = position; }
          position += 1;
        }  
        position += length;

        // Compute match and distance codes.
        int mc = 0; while ( length >= MatchOff[ mc ] ) mc += 1; mc -= 1;
        int dc = 29; while ( distance < DistOff[ dc ] ) dc -= 1;

        if ( prev.Lit.Bits[ 257 + mc ] == 0 || prev.Dist.Bits[ dc ] == 0 ) goto EndSearch;
        delta += prev.Lit.Bits[ 257 + mc ] - Lit.Bits[ 257 + mc  ];
        delta += prev.Dist.Bits[ dc ] - Dist.Bits[ dc ];

        if ( delta < bestDelta ) { bestDelta = delta; bestPosition = position; }
        position += 1;
      }

      while ( position < end ) 
      {
        byte b = d.Input[ position ];
        if ( prev.Lit.Bits[ b ] == 0 ) goto EndSearch;
        delta += prev.Lit.Bits[ b ] - Lit.Bits[ b ];
        if ( delta < bestDelta ) { bestDelta = delta; bestPosition = position; }
        position += 1;
      }  

      EndSearch:
      
      blockSize = bestPosition - prev.Start;
      return bestDelta;
    }

    private void PutCodes( Deflator d )
    {
      byte [] input = d.Input;
      OutBitStream output = d.Output;

      int position = Start;
      int bufferRead = BufferStart;

      while ( bufferRead != BufferEnd )
      {
        int matchPosition = d.PositionBuffer[ bufferRead ];

        int length = d.LengthBuffer[ bufferRead ] + MinMatch;
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
        output.WriteBits( MatchExtra[ mc ], (uint)( length - MatchOff[ mc ] ) );
        output.WriteBits( Dist.Bits[ dc ], Dist.Codes[ dc ] );
        output.WriteBits( DistExtra[ dc ], (uint)(distance-DistOff[ dc ] ) );    
      }

      while ( position < End ) 
      {
        byte b = input[ position ];
        output.WriteBits( Lit.Bits[ b ], Lit.Codes[ b ] );
        position += 1;
      }  
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
          if ( Repeat > 0 ) EncodeRepeat(); 
          ZeroRun += 1; 
          PreviousLength = 0; 
        }
        else if ( length == PreviousLength ) 
        {
          Repeat += 1;
        }
        else 
        { 
          if ( ZeroRun > 0 ) EncodeZeroRun(); 
          if ( Repeat > 0 ) EncodeRepeat(); 
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
  public ushort Count; // Number of symbols.
  public byte [] Bits; // Number of bits used to encode a symbol ( code length ).
  public ushort [] Codes; // Huffman code for a symbol ( bit 0 is most significant ).
  public int [] Used; // Count of how many times a symbol is used in the block being encoded.

  private int Limit; // Limit on code length ( 15 or 7 for RFC 1951 ).
  private ushort [] Left, Right; // Tree storage.

  public HuffmanCoding( int limit, ushort symbols )
  {
    Limit = limit;
    Count = symbols;
    Bits = new byte[ symbols ];
    Codes = new ushort[ symbols ];
    Used = new int[ symbols ];
    Left = new ushort[ symbols ];
    Right = new ushort[ symbols ];
  }

  public int Total()
  {
    int result = 0;
    for ( int i = 0; i < Count; i += 1 ) 
      result += Used[i] * Bits[i];
    return result;
  }

  public void ComputeCodes() // Compute Bits and Codes from Used.
  {
    // Tree nodes are encoded in a ulong using 16 bits for the id, 8 bits for the tree depth, 32 bits for Used.
    const int IdBits = 16, DepthBits = 8, UsedBits = 32;
    const uint IdMask = ( 1u << IdBits ) - 1;
    const uint DepthOne = 1u << IdBits;
    const uint DepthMask = ( ( 1u << DepthBits ) - 1 ) << IdBits;
    const ulong UsedMask = ( ( 1ul << UsedBits ) - 1 ) << ( IdBits + DepthBits );

    // First compute the number of bits to encode each symbol (Bits).
    UlongHeap heap = new UlongHeap( Count );

    for ( ushort i = 0; i < Count; i += 1 )
    {
      int used = Used[ i ];
      if ( used > 0 )
        heap.Add( (ulong)used << ( IdBits + DepthBits ) | i );
    }
    heap.Make();

    int nonZero = heap.Count;
    int maxBits = 0;

    if ( heap.Count == 1 )
    { 
      GetBits( unchecked( (ushort) heap.Remove() ), 1 );
      maxBits = 1;
    }
    else if ( heap.Count > 1 ) unchecked
    {
      ulong treeNode = Count;

      do // Keep pairing the lowest frequency TreeNodes.
      {
        ulong left = heap.Remove(); 
        Left[ treeNode - Count ] = (ushort) left;

        ulong right = heap.Remove(); 
        Right[ treeNode - Count ] = (ushort) right;

        // Extract depth of left and right nodes ( still shifted though ).
        uint depthLeft = (uint)left & DepthMask, depthRight = (uint)right & DepthMask; 

        // New node depth is 1 + larger of depthLeft and depthRight.
        uint depth = ( depthLeft > depthRight ? depthLeft : depthRight ) + DepthOne;

        heap.Insert( ( left + right ) & UsedMask | depth | treeNode );

        treeNode += 1;
      }  while ( heap.Count > 1 );
      
      uint root = (uint) heap.Remove() & ( DepthMask | IdMask );
      maxBits = (int)( root >> IdBits );
      if ( maxBits <= Limit )
        GetBits( (ushort)root, 0 );
      else
      {
        maxBits = Limit;
        PackageMerge( nonZero );
      }
    }

    // Computation of code lengths (Bits) is complete.
    // Now compute Codes, code below is from RFC 1951 page 7.

    int [] bl_count = new int[ maxBits + 1 ];
    for ( int i = 0; i < Count; i += 1 ) 
      bl_count[ Bits[ i ] ] += 1; 

    int [] next_code = new int[ maxBits + 1 ];
    int code = 0; bl_count[ 0 ] = 0;
    for ( int i = 0; i < maxBits; i += 1 ) 
    {
      code = ( code + bl_count[ i ] ) << 1;
      next_code[ i + 1 ] = code;
    }

    for ( int i = 0; i < Count; i += 1 ) 
    {
      int length = Bits[ i ];
      if ( length != 0 ) 
      {
        Codes[ i ] = (ushort) Reverse( next_code[ length ], length );
        next_code[ length ] += 1;
      }
    }

    // Reduce Count if there are unused trailing symbols.
    while ( Count > 0 && Bits[ Count - 1 ] == 0 ) Count -= 1;

  }

  private void GetBits( ushort treeNode, int length )
  {
    if ( treeNode < Count ) // treeNode is a leaf.
    {
      Bits[ treeNode ] = (byte) length;
    }
    else 
    {
      treeNode -= Count;
      length += 1;
      GetBits( Left[ treeNode ], length );
      GetBits( Right[ treeNode ], length );
    }
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

  // PackageMerge is used if the Limit is exceeded.
  // The result is technically not a Huffman code in this case.
  // See https://en.wikipedia.org/wiki/Package-merge_algorithm for a description of the algorithm.

  private void PackageMerge( int usedNonZero )
  {
    // Tree nodes are encoded in a ulong using 16 bits for the id, 32 bits for Used.
    const int IdBits = 16, UsedBits = 32;
    const ulong UsedMask = ( ( 1ul << UsedBits ) - 1 ) << IdBits;

    Left = new ushort[ Count * Limit ];
    Right = new ushort[ Count * Limit ];

    // First create the leaf nodes and sort.

    ulong [] sorted = new ulong[ usedNonZero ];
    for ( uint i = 0, j = 0; i < Count; i += 1 ) 
    {
      if ( Used[ i ] != 0 ) 
      {
        sorted[ j++ ] = (ulong) Used[ i ] << IdBits | i;
      }
    }
    System.Array.Sort( sorted );

    // Sort is complete.

    // List class is from System.Collections.Generic.
    Generic.List<ulong> merged = new Generic.List<ulong>( Count ), 
                next = new Generic.List<ulong>( Count );

    uint package = (uint) Count; // Allocator for package ids.

    for ( int i = 0; i < Limit; i += 1 ) 
    {
      int j = 0, k = 0; // Indexes into the lists being merged.
      next.Clear();
      for ( int total = ( sorted.Length + merged.Count ) / 2; total > 0; total -= 1 )  
      {
        ulong left, right; // The tree nodes to be packaged.

        if ( k < merged.Count )
        {
          left = merged[ k ];
          if ( j < sorted.Length )
          {
            ulong sj = sorted[ j ];
            if ( left < sj ) k += 1;
            else { left = sj; j += 1; }
          }
          else k += 1;
        }
        else left = sorted[ j++ ];

        if ( k < merged.Count )
        {
          right = merged[ k ];
          if ( j < sorted.Length )
          {
            ulong sj = sorted[ j ];
            if ( right < sj ) k += 1;
            else { right = sj; j += 1; }
          }
          else k += 1;
        }
        else right = sorted[ j++ ];

        Left[ package ] = unchecked( (ushort) left );
        Right[ package ] = unchecked( (ushort) right );
        next.Add( ( left + right ) & UsedMask | package );        
        package += 1;
      }

      // Swap merged and next.
      Generic.List<ulong> tmp = merged; merged = next; next = tmp;
    }

    for ( int i = 0; i < merged.Count; i += 1 )
      MergeGetBits( unchecked( (ushort) merged[i] ) );
  }

  private void MergeGetBits( ushort node )
  {
    if ( node < Count )
      Bits[ node ] += 1;
    else
    {
      MergeGetBits( Left[ node ] );
      MergeGetBits( Right[ node ] );
    }
  }

} // end struct HuffmanCoding


// ******************************************************************************


struct UlongHeap // An array organised so the smallest element can be efficiently removed.
{
  public int Count { get{ return _Count; } }
  private int _Count;
  private ulong [] Array;

  /* Diagram showing numbering of tree elements.
           0
       1       2
     3   4   5   6
  */

  public UlongHeap ( int capacity )
  {
    _Count = 0;
    Array = new ulong[ capacity ];
  }
  
  public void Insert( ulong e )
  {
    int j = _Count++;
    while ( j > 0 )
    {
      int p = ( j - 1 ) >> 1; // Index of parent.
      ulong pe = Array[ p ];
      if ( e >= pe ) break;
      Array[ j ] = pe; // Demote parent.
      j = p;
    }    
    Array[ j ] = e;
  }

  public ulong Remove() // Returns the smallest element.
  {
    ulong result = Array[ 0 ];
    _Count -= 1;
    ulong e = Array[ _Count ];
    int j = 0;
    while ( true )
    {
      int c = j + j + 1; if ( c >= _Count ) break;
      ulong ce = Array[ c ];
      if ( c + 1 < _Count )
      {
        ulong ce2 = Array[ c + 1 ];
        if ( ce2 < ce ) { c += 1; ce = ce2; }
      } 
      if ( ce >= e ) break;
      Array[ j ] = ce; j = c;  
    }
    Array[ j ] = e;
    return result;
  }

  // Add and Make allow the heap to be initialised faster.

  public void Add( ulong e )
  {
    Array[ _Count++ ] = e;
  }

  public void Make()
  {
    // Initialise the heap by making every parent less than both it's children.
    int p = ( _Count - 2 ) / 2; // parent of last element.
    while ( p >= 0 )
    {
      // Bubble element at p down.
      int j = p;
      ulong e = Array[ j ];
      while ( true )
      {
        int c = j + j + 1; if ( c >= _Count ) break;
        ulong ce = Array[ c ];
        if ( c + 1 < _Count )
        {
          ulong ce2 = Array[ c + 1 ];
          if ( ce2 < ce ) { c += 1; ce = ce2; }
        }
        if ( ce >= e ) break;
        Array[ j ] = ce; j = c;
      }
      Array[ j ] = e;
      p -= 1;   
    }
  }

} // end struct UlongHeap


// ******************************************************************************


abstract class OutBitStream
{
  public void WriteBits( int n, uword value )
  // Write first n bits of value to stream, least significant bit is written first.
  // Unused bits of value must be zero, i.e. value must be in range 0 .. 2^n-1.
  {
    if ( n + BitsInWord >= WordCapacity )
    {
      Save( value << BitsInWord | Word );
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

  protected abstract void Save( uword word );

  protected const int WordCapacity = sizeof( uword ) * 8; // Number of bits that can be stored in Word
  protected uword Word; // Bits are first stored in Word, when full, Word is saved.
  protected int BitsInWord; // Number of bits currently stored in Word.
 
}


// ******************************************************************************


class MemoryBitStream : OutBitStream
{
  // An implementation of OutBitStream where the bits are stored in memory.
  // The storage is a linked list of Chunks.
  // ByteSize returns the current size in bytes.
  // CopyTo copies the contents to a Stream.
  // ToArray returns the contents as an array of bytes.

  public int ByteSize() 
  {
    return CompleteChunks * Chunk.Capacity + BytesInCurrentChunk + ( BitsInWord + 7 ) / 8;
  }

  public void CopyTo( System.IO.Stream s ) 
  {
    for ( Chunk c = FirstChunk; c != null; c = c.Next )
    { 
      int n = ( c == CurrentChunk ) ? BytesInCurrentChunk : Chunk.Capacity;
      s.Write( c.Bytes, 0, n ); 
    }

    int biw = BitsInWord;
    uword word = Word;
    while ( biw > 0 )
    {
      s.WriteByte( unchecked( (byte) word ) );
      word >>= 8;
      biw -= 8;
    }
  }

  public byte [] ToArray()
  {
    byte [] result = new byte[ ByteSize() ];
    int i = 0;

    for ( Chunk c = FirstChunk; c != null; c = c.Next )
    { 
      int n = ( c == CurrentChunk ) ? BytesInCurrentChunk : Chunk.Capacity;
      System.Array.Copy( c.Bytes, 0, result, i, n ); 
      i += n;
    }

    int biw = BitsInWord;
    uword word = Word;
    while ( biw > 0 )
    {
      result[ i++ ] = unchecked( (byte) word );
      word >>= 8;
      biw -= 8;
    }
    return result;
  }

  public MemoryBitStream()
  {
    FirstChunk = new Chunk();
    CurrentChunk = FirstChunk;
  }

  protected override void Save( uword w )
  {
    if ( BytesInCurrentChunk == Chunk.Capacity )
    {
      Chunk nc = new Chunk();
      CurrentChunk.Next = nc;
      CurrentChunk = nc;
      CompleteChunks += 1;
      BytesInCurrentChunk = 0;
    }
    int i = BytesInCurrentChunk;
    byte [] bytes = CurrentChunk.Bytes;
    unchecked
    {
      #if x32
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w;
      #else
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w; w >>= 8;
      bytes[ i++ ] = (byte) w;
      #endif
    }
    BytesInCurrentChunk = i;
  }

  protected class Chunk
  {
    public const int Capacity = 0x800;
    public byte [] Bytes = new byte[ Capacity ];
    public Chunk Next;
  }

  protected int BytesInCurrentChunk;
  protected int CompleteChunks;
  protected Chunk FirstChunk, CurrentChunk;

} // end class MemoryBitStream


} // end namespace Pdf
