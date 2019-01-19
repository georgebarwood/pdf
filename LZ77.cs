namespace Pdf
{

class LZ77 // Implements LZ77 compression per RFC 1951. For use in Deflator class.
{
  public delegate int SaveMatch( int position, int distance, int length );

  public static void Process( byte [] input, SaveMatch output )
  {
    if ( input.Length < MinMatch ) return;
    LZ77 lz = new LZ77( input );
    lz.Go( output );
  }

  // Rest is private.

  // RFC 1951 limits.
  private const int MinMatch = 3;
  private const int MaxMatch = 258;
  private const int MaxDistance = 0x8000;

  // Fields.
  private readonly byte [] Input;

  private int Match, Distance;

  // Functions.

  private void Go( SaveMatch output )
  {
    byte [] input = Input;
    int limit = input.Length - 2;
    int hashShift = CalcHashShift( limit * 2 );
    uint hashMask = ( 1u << ( MinMatch * hashShift ) ) - 1;
    Bucket [] hashTable = new Bucket[ hashMask + 1 ];

    int skip = 0;
    int pendingMatch = 0;
    int pendingDistance = 0;

    uint hash = ( (uint)input[ 0 ] << hashShift ) + input[ 1 ];

    for ( int position = 0; position < limit; position += 1 )
    {
      hash = ( ( hash << hashShift ) + input[ position + 2 ] ) & hashMask;
      Match = 0;
      Bucket bucket = hashTable[ hash ];
      hashTable[ hash ] = 
          ( bucket == null ) ? new SingleBucket( position )
        : ( position >= skip ) ? bucket.Match( this, position )
        : bucket.Add( position );

      if ( pendingMatch >= MinMatch )
      {
        if ( Match <= pendingMatch && ( Match != pendingMatch || Distance >= pendingDistance ) )
        {
          Match = 0;
          skip = output( position - 1, pendingMatch, pendingDistance );
        }
      }
      pendingMatch = Match;
      pendingDistance = Distance;
    }

    if ( pendingMatch >= MinMatch ) output( limit-1, pendingMatch, pendingDistance );
  }

  private int CalcHashShift( int n )
  {
    // Size of hash table will be 8 ^ HashShift ( 8, 0x40, 0x200, 0x1000, 0x8000, 0x40000 ).
    int p8 = 1;
    int result = 0;
    while ( n > p8 ) 
    {
      p8 = p8 << MinMatch;
      result += 1;
      if ( result == 6 ) break;
    }
    return result;
  }    

  private static int MatchLength( byte [] input, int p, int q )
  {
    int n = input.Length;
    if ( n - p > MaxMatch ) n = p + MaxMatch;
    int pstart = p;
    while ( p < n && input[ p ] == input [ q ] )
    {
      p += 1;
      q += 1;
    }
    return p - pstart;
  }

  private LZ77( byte [] input )
  {
    Input = input;
  }

  // Classes.

  private abstract class Bucket
  {
    public abstract Bucket Add( int position );
    public abstract Bucket Match( LZ77 lz, int position ); 
  }

  private class SingleBucket : Bucket // A bucket that holds a single position.
  {
    private int Position;

    public SingleBucket( int position ) 
    { 
      Position = position; 
    }

    public override Bucket Add( int position )
    {
      if ( position - Position > MaxDistance )
      {
        Position = position;
        return this;
      }
      else
      {
        return new ListBucket( position, new ListBucket( Position, null ) );
      }
    }

    public override Bucket Match( LZ77 lz, int position )
    { 
      int distance = position - Position;
      if ( distance > MaxDistance )
      {
        Position = position;
        return this;
      }
      else
      {   
        lz.Distance = distance;
        lz.Match = MatchLength( lz.Input, position, Position );
        return new ListBucket( position, new ListBucket( Position, null ) );
      }
    } 

  }

  private class ListBucket : Bucket // A bucket that holds multiple positions.
  {
    int Position;
    ListBucket Next;

    public ListBucket( int position, ListBucket next )
    { 
      Position = position; 
      Next = next;  
    }

    public override Bucket Add( int position )
    {
      return new ListBucket( position, this );
    }

    public override Bucket Match( LZ77 lz, int position )
    { 
      int limit = position - MaxDistance;
      int oldPosition = Position;
      if ( oldPosition < limit ) return new SingleBucket( position );

      byte [] input = lz.Input;
      int avail = input.Length - position;
      if ( avail > MaxMatch ) avail = MaxMatch;

      int bestMatch = 0;
      int bestDistance = 0;
      ListBucket old = this;
      while ( true )
      { 
        if ( input[ position + bestMatch ] == input[ oldPosition + bestMatch ] )
        {
          int match = MatchLength( input, position, oldPosition );
          if( match > bestMatch )
          {
            bestMatch = match;
            bestDistance = position - oldPosition;
            if ( bestMatch == avail ) break;
          }
        }

        old = old.Next;
        if ( old == null ) break;

        oldPosition = old.Position;
        if ( oldPosition < limit ) break;
      }

      lz.Match = bestMatch;
      lz.Distance = bestDistance;

      return new ListBucket( position, this );
    }

  } // end class ListBucket

} // end class LZ77

} // namespace
