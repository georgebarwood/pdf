namespace Pdf {

public abstract class OutBitStream
{
  // Write first n ( 0 <= n <= 64 ) bits of value to stream, least significant bit is written first.
  // Unused bits of value must be zero, i.e. value must be in range 0 .. 2^n-1.
  public abstract void WriteBits( int n, ulong value ); 

  // Pad with zero bits to n bit boundary where n is power of 2 in range 1,2,4..64, typically n=8.
  public abstract void Pad( int n ); 
}

class BitBuffer : OutBitStream
{
  // BitBuffer buffers a stream of bits. There is no limit on the number of bits held.
  // ByteSize returns the current size in bytes, CopyTo copies the contents to a Stream.

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
      for ( int i = 0; i < n; i += 1 ) 
      {
        ulong w = c.Words[ i ];
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

  public BitBuffer()
  {
    FirstChunk = new Chunk();
    CurrentChunk = FirstChunk;
  }

  // Private constants, fields, classes.

  const int WordSize = sizeof(ulong);  // Size of Word in bytes.
  const int WordCapacity = WordSize * 8; // Number of bits that can be stored Word

  ulong Word; // Bits are first stored in Word, when full, Word is copied to CurrentChunk.
  int BitsInWord; // Number of bits stored in Word.
  int WordsInCurrentChunk; // Number of words stored in CurrentChunk.
  int CompleteChunks; // Number of complete Chunks.
  Chunk FirstChunk, CurrentChunk;

  private class Chunk
  {
    public const int Capacity = 256;
    public ulong [] Words = new ulong[ Capacity ];
    public Chunk Next;
  }

  // OutBitStream implementation.

  public override void WriteBits( int n, ulong value )
  {
    if ( n + BitsInWord >= WordCapacity )
    {
      Word |= value << BitsInWord;
      int space = WordCapacity - BitsInWord;
      value >>= space;
      n -= space;
      if ( WordsInCurrentChunk == Chunk.Capacity )
      {
        Chunk nc = new Chunk();
        CurrentChunk.Next = nc;
        CurrentChunk = nc;
        CompleteChunks += 1;
        WordsInCurrentChunk = 0;
      }
      CurrentChunk.Words[ WordsInCurrentChunk++ ] = Word;
      Word = 0;
      BitsInWord = 0;
    }
    Word |= value << BitsInWord;
    BitsInWord += n;
  }

  public override void Pad( int n )
  {
    int w = BitsInWord % n; 
    if ( w > 0 ) WriteBits( n - w, 0 );
  }

} // end class BitBuffer

} // namespace
