using Generic = System.Collections.Generic; 

namespace Pdf {

public class Inflator : InpBitStream
{
  int TraceLevel = 0;
  int BlockCount;

  public Generic.List<byte> Go ( byte [] input ) // RFC 1951
  {
    IB = input; Ix = 2; // Skip 1st 2 bytes ( RFC 1950 )
    uint bfinal; 
    do
    { 
      bfinal = GetBit();
      uint btype = GetBits(2);
      if ( TraceLevel > 0 ) 
      {
        BlockCount += 1;
        if( TraceLevel > 1 ) System.Console.WriteLine( "btype=" + btype + " at " + OB.Count );
      }
      switch ( btype ){ case 0: DoCopy(); break; case 1: DoFixed(); break; case 2: DoDyn(); break; }
    } while ( bfinal == 0 );

    if ( TraceLevel > 0 )
      System.Console.WriteLine( "Inflator.Go, input size=" + input.Length + " output size=" + OB.Count + " blockcount=" + BlockCount );
    return OB;
  }

  void DoDyn() // Dynamic huffman encoding, the most complex case, RFC1951 page 12.
  {
    uint nLitCode = 257+GetBits(5), nDistCode = 1+GetBits(5), nLenCode = 4+GetBits(4);

    for ( uint i = 0; i < nLenCode; i += 1 ) ClenLen[ ClenAlphabet[i] ] = (byte)GetBits(3);
    for ( uint i = nLenCode; i < 19; i += 1 ) ClenLen[ ClenAlphabet[i] ] = 0;
    Clen.MakeTree( ClenLen, 19 );
    
    Plenc = 0; uint carry = GetLengths( LitLen, nLitCode, 0 ); GetLengths( DistLen, nDistCode, carry ); 
    Lit.MakeTree( LitLen, nLitCode );
    Dist.MakeTree( DistLen, nDistCode );

    while (true)
    {
      uint x = Lit.Decode( this );
      if ( x < 256 ) OB.Add( (byte) x ); 
      else if ( x == 256 ) break;
      else
      {
        x -= 257;
        uint length = MatchOff[ x ] + GetBits( MatchExtra[ x ] );
        uint dc = Dist.Decode( this );
        uint distance = DistOff[ dc ] + GetBits( DistExtra[ dc ] );
        if ( TraceLevel > 1 ) System.Console.WriteLine( "Copy at " + OB.Count + " length=" + length + " distance=" + distance  );
        OB.Copy( distance, length ); 
      }
    }
  }  // end DoDyn

  public readonly OutputBuffer OB = new OutputBuffer();

  readonly HuffD Clen = new HuffD(), Lit = new HuffD(), Dist = new HuffD();
  readonly byte [] ClenLen = new byte[19], LitLen = new byte[288], DistLen = new byte[32];

  uint Plenc; // RFC 1951 : "the code length repeat codes can cross from HLIT + 257 to the HDIST + 1 code lengths." 

  // Data per RFC 1951
  static readonly byte [] ClenAlphabet = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 }; // size = 19
  static readonly byte [] MatchExtra = { 0,0,0,0, 0,0,0,0, 1,1,1,1, 2,2,2,2, 3,3,3,3, 4,4,4,4, 5,5,5,5, 0 }; // size = 29
  static readonly ushort [] MatchOff = { 3,4,5,6, 7,8,9,10, 11,13,15,17, 19,23,27,31, 35,43,51,59, 67,83,99,115, 131,163,195,227, 258 }; // also 29
  static readonly byte [] DistExtra = { 0,0,0,0, 1,1,2,2, 3,3,4,4, 5,5,6,6, 7,7,8,8, 9,9,10,10, 11,11,12,12, 13,13 }; // size = 30
  static readonly ushort [] DistOff = { 1,2,3,4, 5,7,9,13, 17,25,33,49, 65,97,129,193, 257,385,513,769, 1025,1537,2049,3073,
     4097,6145,8193,12289, 16385,24577 }; // also 30

  uint GetLengths( byte [] la, uint n, uint rep ) // Per RFC1931 page 13.
  {
    uint i = 0;
    while ( rep > 0 ) { la[i++] = (byte)Plenc; rep -= 1; }
    while ( i < n )
    { 
      uint lenc = Clen.Decode( this );
      if ( lenc < 16 ) { la[i++] = (byte)lenc; Plenc = lenc; }
      else 
      {
        if ( lenc == 16 ) { rep = 3+GetBits(2); }
        else if ( lenc == 17 ) { rep = 3+GetBits(3); Plenc=0; }
        else if ( lenc == 18 ) { rep = 11+GetBits(7); Plenc=0; } 
        while ( i < n && rep > 0 ) { la[i++] = (byte)Plenc; rep -= 1; }
      }
    }
    return rep;
  } // end GetLengths

  void DoFixed() // RFC1951 page 12.
  {
    while (true)
    {
      // 0 to 23 ( 7 bits ) => 256 - 279; 48 - 191 ( 8 bits ) => 0 - 143; 
      // 192 - 199 ( 8 bits ) => 280 - 287; 400..511 ( 9 bits ) => 144 - 255
      uint x = GetHuff( 7 ); 
      if ( x <= 23 ) x += 256;
      else
      {
        x = ( x << 1 ) + GetBit();
        if ( x <= 191 ) x -= 48;
        else if ( x <= 199 ) x += 88;
        else x = ( x << 1 ) + GetBit() - 256;
      }

      if ( x < 256 ) OB.Add( (byte) x );
      else if ( x == 256 ) break;
      else // ( 257 <= x && x <= 285 ) 
      {
        x -= 257;
        uint length = MatchOff[x] + GetBits( MatchExtra[ x ] );
        uint dcode = GetHuff( 5 );
        uint distance = DistOff[dcode] + GetBits( DistExtra[dcode] );
        if ( TraceLevel > 1 ) System.Console.WriteLine( "Copy at " + OB.Count + " length=" + length + " distance=" + distance  );
        OB.Copy( distance, length );
      }
    }
  } // end DoFixed

  void DoCopy()
  {
    ClearBits(); // Discard any bits in the input buffer
    uint n = Get16();
    Ix += 2 ; // uint n1 = Get16();
    while ( n-- > 0 ) OB.Add( IB[Ix++] );
  }

  public static uint Adler32( byte [] b )
  {
    uint s1=1, s2=0;
    for ( int i=0; i<b.Length; i+= 1)
    {
      s1 = ( s1 + b[i] ) % 65521;
      s2 = ( s2 + s1 ) % 65521;
    }
    return s2*65536 + s1;    
  }
} // enc class Decoder

//+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

public class OutputBuffer : Generic.List<byte>
{
/*
  public byte [] Debug;

  public new void Add( byte b )
  {
    if ( Debug != null && Debug[Count] != b )
    {
      System.Console.WriteLine( "Debug error Count=" + Count );
      throw new System.Exception();
    }
    base.Add( b );
  }
*/

  public void Copy( uint dist, uint n )
  {
    int srcIx = (int)(Count - dist);
    while ( n > 0 ) { Add( this[srcIx++] ); n -= 1; }
  }
} // end class OutputBuffer

//+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

public class InpBitStream
{
  public byte [] IB; // Input bytes. May want to make this a System.IO.Stream, but for PDF writing purposes this is fine.
  public uint Buf = 1, Ix = 0;
  
  public uint GetBit(){ if ( Buf == 1 ) { Buf = IB[Ix++] | 256u; }  uint b = Buf & 1u; Buf >>= 1; return b; }

  public void ClearBits(){ Buf = 1; }

  public uint GetBits( int n ) // Get bits, least sig bit first
  { uint result = 0; for ( int i=0; i<n; i+= 1 ) result += GetBit() << i; return result; }

  public uint GetHuff( int n ) // Get bits, most sig bit first
  { uint result = 0; for ( int i=0; i<n; i+= 1 ) result = ( result << 1 ) + GetBit(); return result; }

  public uint Get16(){ uint result = ( (uint)IB[Ix+1] << 8 ) + IB[Ix]; Ix += 2; return result; }

} // end class InpBitStream

//+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

class HuffD // Huffman Decoder
{
  public uint Decode( InpBitStream inp ) // This could be optimised, but it's simple and easy to understand like this!
  { Node n = Root;  while ( n.Left != null ) n = inp.GetBit() == 0 ? n.Left : n.Right;  return n.Value; }  

  private class Node { public Node Left, Right; public uint Value; }
  private Node Root;

  public void MakeTree( byte [] nbits, uint ncode ) // bits is number of bits to be used for each code.
  {
    Root = null; // Maybe this could be omitted if Insert cleared Left when setting Value.

    // Code below is from rfc1951 page 7

    uint maxBits = 0; for ( uint i=0; i < ncode; i += 1 ) if ( nbits[i] > maxBits ) maxBits = nbits[i];

    uint [] bl_count = new uint[maxBits+1];
    for ( uint i=0; i < ncode; i += 1 ) bl_count[ nbits[i] ] += 1;

    uint [] next_code = new uint[maxBits+1];
    uint code = 0; bl_count[0] = 0;
    for ( uint i = 0; i < maxBits; i += 1 ) 
    {
      code = ( code + bl_count[i] ) << 1;
      next_code[i+1] = code;
    }

    uint [] tree_code= new uint[ncode];
    for (uint i = 0; i < ncode; i += 1 ) 
    {
      uint len = nbits[i];
      if (len != 0) 
      {
        tree_code[i] = next_code[len];
        next_code[len] += 1;
      }
    }

    // Console.WriteLine( "Huff Code" );
    // for ( uint i=0; i < ncode; i += 1 ) if ( nbits[i] > 0 )
    //  Console.WriteLine( "i=" + i + " len=" + nbits[i] + " tc=" + tree_code[i].ToString("X") );

    for ( uint i=0; i < ncode; i += 1 ) if ( nbits[i] > 0 )
      Root = Insert( Root, i, nbits[i], tree_code[i] );
  }

  static Node Insert( Node x, uint value, int len, uint code )
  {
    if ( x == null ) x = new Node();
    if ( len == 0 ) x.Value = value;
    else if ( ( code >> (len-1) & 1 ) == 0 ) 
      x.Left = Insert( x.Left, value, len-1, code );
    else 
      x.Right = Insert( x.Right, value, len-1, code );  
    return x;
  }
} // End class HuffD

} // namespace
