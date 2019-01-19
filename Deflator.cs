namespace Pdf {

class Deflator // Implements RFC 1950 / 1951 "Deflate" compression, using HuffEncoder and LZ77.
{
  public static void Deflate( byte [] input, OutBitStream output )
  {
    Deflator d = new Deflator( input, output );
    output.WriteBits( 16, 0x9c78 ); // RFC 1950 bytes.
    LZ77.Process( input, d.SaveMatch );
    d.Buffered = input.Length;
    while ( !d.OutputBlock( true ) );
    output.Pad( 8 );
    output.WriteBits( 32, Adler32( input ) ); // RFC 1950 checksum.   
  }

  // Private constants.

  private const int BlockSize  = 0x1000; // Initial blocksize, actual may be larger or smaller. Need not be power of two.
  private const int MaxBufferSize = 0x8000; // Must be power of 8.

  private int BufferMask;

  // Private fields.

  private byte [] Input;
  private OutBitStream Output;

  private int Buffered; // How many Input bytes have been processed to intermediate buffer.
  private int Finished; // How many Input bytes have been written to Output.

  // Intermediate circular buffer for LZ77 matches.
  private int I, J;
  private int    [] PositionBuffer;
  private ushort [] LengthBuffer;
  private ushort [] DistanceBuffer;

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
  {
    if ( n >= max ) return max;
    int result = 1;
    while ( result < n ) result = result << 1;
    return result;
  }

  private int SaveMatch ( int position, int length, int distance ) // Called from LZ77 class.
  {
    // System.Console.WriteLine( "Save at " + position + " length=" + length + " distance=" + distance );
    int i = I;
    PositionBuffer[ i ] = position;
    LengthBuffer[ i ] = (ushort) length;
    DistanceBuffer[ i ] = (ushort) distance;
    i = ( i + 1 ) & BufferMask;
    if ( i == J ) OutputBlock( false );
    I = i;
    position += length;
    Buffered = position;
    return position;
  }

  private bool OutputBlock( bool last )
  {
    int amount = Buffered - Finished;
    
    if ( amount > BlockSize ) 
    {
      amount = ( last && amount < BlockSize*2 ) ? amount >> 1 : BlockSize;
    }

    int c1; // Bit size of DynBlock
    DynBlock db1;

    while ( true ) // While DynBlock constructor fails, reduce input amount.
    {
      db1 = new DynBlock( this, amount, null, out c1 );
      if ( c1 >= 0 ) break;
      amount -= amount / 3;
    }     

    while ( db1.End < Buffered ) // Try to increase block size.
    {
      int c2; DynBlock db2 = new DynBlock( this, amount, db1, out c2 );
      if ( c2 < 0 ) break;

      int c3; DynBlock db3 = new DynBlock( this, db2.End - db1.Start, null, out c3 );
      if ( c3 < 0 ) break;

      // If combination uses more bits than two smaller blocks individually, give up.
      if ( c3 > c1 + c2 ) break;

      c1 = c3;
      db1 = db3;
      amount += amount; 
    }      

    if ( db1.End < Buffered ) last = false;
    db1.WriteBlock( this, last );  
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

  private class DynBlock
  {
    public readonly int Start, End; // Range of Deflator Input encoded by block.

    public DynBlock( Deflator d, int amount, DynBlock previous, out int bits )
    {
      Output = d.Output;
      bits = -1;

      if ( previous == null )
      {
        Start = d.Finished;
        J = d.J;
      }
      else
      {
        Start = previous.End;
        J = previous.EndJ;
      }

      int avail = d.Buffered - Start;
      if ( amount > avail ) amount = avail;

      LitFreq = new int[288];
      DistFreq = new int[32]; 

      End = TallyFrequencies( d, amount );
      LitFreq[ 256 ] += 1; // End of block code.
     
      LitLen = new byte [ 288 ]; 
      LitCode = new ushort [ 288 ]; 

      HLit = HuffEncoder.ComputeCodes( 15, LitFreq, LitLen, LitCode ); 
      if ( HLit < 0 ) return;

      DistLen = new byte [ 32 ]; 
      DistCode = new ushort [ 32 ];
      HDist = HuffEncoder.ComputeCodes( 15, DistFreq, DistLen, DistCode ); 
      if ( HDist < 0 ) return;

      if ( HDist == 0 ) HDist = 1;

      // Compute length encoding.
      LenFreq = new int [ 19 ];
      DoLengthPass( 1 );
      LenLen = new byte [ 19 ]; 
      LenCode = new ushort[ 19 ];
      if ( HuffEncoder.ComputeCodes( 7, LenFreq, LenLen, LenCode ) < 0 ) return;

      HCLen = 19; while ( HCLen > 4 && LenLen[ ClenAlphabet[ HCLen - 1 ] ] == 0 ) HCLen -= 1;

      // Compute block size in bits ( doesn't include extra data bits ).
      bits = 17 + 3 * HCLen;
      for ( int i=0; i < HCLen; i += 1 ) bits += LenFreq[ i ]  * LenLen[ i ];
      for ( int i=0; i < HLit;  i += 1 ) bits += LitFreq[ i ]  * LitLen[ i ];
      for ( int i=0; i < HDist; i += 1 ) bits += DistFreq[ i ] * DistLen[ i ];
    }

    public void WriteBlock( Deflator d, bool last )
    {
      OutBitStream output = Output;
      output.WriteBits( 1, last ? 1u : 0u );
      output.WriteBits( 2, 2 );
      output.WriteBits( 5, (uint)(HLit - 257) ); 
      output.WriteBits( 5, (uint)(HDist - 1) ); 
      output.WriteBits( 4, (uint)(HCLen - 4) );

      for ( int i = 0; i < HCLen; i += 1 ) 
        output.WriteBits( 3, LenLen[ ClenAlphabet[ i ] ] );

      DoLengthPass( 2 );
      PutCodes( d );
      output.WriteBits( LitLen[ 256 ], LitCode[ 256 ] ); // End of block code
    }

    // Private fields and constants.

    private OutBitStream Output;
    private int J, EndJ;

    // Huffman coding arrays : Lit = Literal or Match Code, Dist = Distance, Freq = Frequency.
    private int   [] LitFreq, DistFreq, LenFreq;
    private byte  [] LitLen,  DistLen,  LenLen;
    private ushort[] LitCode, DistCode, LenCode;

    // Counts for code length encoding.
    private int HLit, HDist, HCLen, LengthPass, PreviousLength, ZeroRun, Repeat;

    // RFC 1951 constants.
    private readonly static byte [] ClenAlphabet = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
    private readonly static byte [] MatchExtra = { 0,0,0,0, 0,0,0,0, 1,1,1,1, 2,2,2,2, 3,3,3,3, 4,4,4,4, 5,5,5,5, 0 };
    private readonly static ushort [] MatchOff = { 3,4,5,6, 7,8,9,10, 11,13,15,17, 19,23,27,31, 35,43,51,59, 67,83,99,115, 
      131,163,195,227, 258, 0xffff };
    private readonly static byte [] DistExtra = { 0,0,0,0, 1,1,2,2, 3,3,4,4, 5,5,6,6, 7,7,8,8, 9,9,10,10, 11,11,12,12, 13,13 };
    private readonly static ushort [] DistOff = { 1,2,3,4, 5,7,9,13, 17,25,33,49, 65,97,129,193, 257,385,513,769, 
      1025,1537,2049,3073, 4097,6145,8193,12289, 16385,24577, 0xffff };

    // Private functions.

    private int TallyFrequencies( Deflator d, int amount )
    {
      int position = Start;
      int end = position + amount;

      int j = J;
      while ( position < end && j != d.I )
      {
        int matchPosition = d.PositionBuffer[j];
        if ( matchPosition >= end ) break;

        int length = d.LengthBuffer[j];
        int distance = d.DistanceBuffer[j];
        j = ( j + 1 ) & d.BufferMask;

        byte [] input = d.Input;
        while ( position < matchPosition ) 
        {
          LitFreq[ input[ position ] ] += 1;
          position += 1;
        }

        position += length;

        int mc = 0; while ( length >= MatchOff[ mc ] ) mc += 1; mc -= 1;
        int dc = 0; while ( distance >= DistOff[ dc ] ) dc += 1; dc -= 1;
        LitFreq[ 257 + mc ] += 1;
        DistFreq[ dc ] += 1;     
      }

      while ( position < end ) 
      {
        LitFreq[ d.Input[ position ] ] += 1;
        position += 1;
      }
 
      EndJ = j;
      return position;
    }

    private void PutCodes( Deflator d )
    {
      byte [] input = d.Input;
      OutBitStream output = d.Output;

      int position = Start;
      int end = End;
      int j = J;
      while ( position < end && j != d.I )
      {
        int matchPosition = d.PositionBuffer[j];

        if ( matchPosition >= end ) break;

        int length = d.LengthBuffer[j];
        int distance = d.DistanceBuffer[j]; 

        j = ( j + 1 ) & d.BufferMask;

        while ( position < matchPosition ) 
        {
          byte b = d.Input[ position ];
          output.WriteBits( LitLen[ b ], LitCode[ b ] );
          position += 1;
        }  
        position += length;

        int mc = 0; while ( length >= MatchOff[ mc ] ) mc += 1; mc -= 1;
        int dc = 0; while ( distance >= DistOff[ dc ] ) dc += 1; dc -= 1;

        output.WriteBits( LitLen[ 257 + mc ], LitCode[ 257 + mc ] );
        output.WriteBits( MatchExtra[ mc ], (uint)(length-MatchOff[ mc ]) );
        output.WriteBits( DistLen[ dc ], DistCode[ dc ] );
        output.WriteBits( DistExtra[ dc ], (uint)(distance-DistOff[ dc ]) );    
      }

      while ( position < end ) 
      {
        byte b = input[ position ];
        output.WriteBits( LitLen[ b ], LitCode[ b ] );
        position += 1;
      }  
      d.J = j;
      d.Finished = position;
    }

    // Run length encoding of code lengths - RFC 1951, page 13.

    private void DoLengthPass( int pass )
    {
      LengthPass = pass; 
      EncodeLengths( HLit, LitLen, true );     
      EncodeLengths( HDist, DistLen, false );
    }

    private void PutLength( int code ) 
    { 
      if ( LengthPass == 1 ) 
        LenFreq[ code ] += 1; 
      else 
        Output.WriteBits( LenLen[ code ], LenCode[ code ] ); 
    }

    private void EncodeLengths( int n, byte [] a, bool isLit )
    {
      if ( isLit ) 
      { 
        PreviousLength = 0; 
        ZeroRun = 0; 
        Repeat = 0; 
      }
      for ( int i = 0; i < n; i += 1 )
      {
        int length = a[ i ];
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
  } // end class DynBlock

} // end class Deflator

} // namespace
