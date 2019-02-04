using String = System.String; using Char = System.Char; 
using IO = System.IO;
using Generic = System.Collections.Generic;

namespace Pdf {

public class Util // Various misc. functions
{
  public static byte [] Inflate( byte[] data )
  { 
    /*
    if ( UseZLib )
    {
      IO.MemoryStream inps = new IO.MemoryStream( data );
      Zlib.ZInflaterInputStream z = new Zlib.ZInflaterInputStream( inps );
      IO.MemoryStream outp = new IO.MemoryStream();
      z.CopyTo( outp, 512 );
      return outp.ToArray();
    }
    else 
    */
    return (new Inflator()).Go( data ).ToArray();
  }

  public static byte[] GetBuf( int need )
  { int n = 512; while ( n < need ) n *= 2; return new byte[n]; }

  public static void Skip( IO.Stream s, int n ) { while ( n > 0 ) { s.ReadByte(); n -= 1; } }

  public static byte[] GetFile( String path )
  {
    // Console.WriteLine( "GetFile " + path );
    IO.MemoryStream ms = new IO.MemoryStream();
    using( IO.FileStream f = IO.File.OpenRead( path) )
    {
      // f.CopyTo( ms );
      byte[] buffer = new byte[0x1000];
      int read;
      while ( ( read = f.Read(buffer, 0, buffer.Length ) ) > 0 )
      {
        ms.Write (buffer, 0, read);
      }
    }
    return ms.ToArray();
  }

  public static void WriteFile( String path, byte [] data )
  {
    // Console.WriteLine( "WriteFile " + path );
    if ( IO.File.Exists( path ) ) IO.File.Delete( path );
    using( IO.FileStream f = IO.File.Create( path ) )
    {
      f.Write( data, 0, data.Length );
    }
  }

  public static void ReadN( IO.Stream inp, byte[] b, int offset, int count )
  {
    while ( count > 0 )
    {
      int n = inp.Read( b, offset, count );
      if ( n <= 0 ) throw new IO.IOException();
      count -= n;
      offset += n;
    }
  }

  public static void Copy( IO.Stream src, IO.Stream dest, int n,byte [] buffer )
  {
    while ( n>0 )
    {
      int size = n;
      if ( size > buffer.Length ) size = buffer.Length;
      size = src.Read( buffer, 0, size );
      dest.Write( buffer, 0, size );
      n -= size;
    }
  }

} // class Util

} // namespacec
