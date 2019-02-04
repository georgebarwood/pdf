using Math = System.Math; using Exception = System.Exception;
using Stream = System.IO.Stream; using MemoryStream = System.IO.MemoryStream;

namespace Pdf.Png { 

public class Reader // Class for reading a Png (portable network graphics) file and translating it to Pdf format.
{

  // public interface, reads PNG file, returns a Pdf.IntImage.

  public static Pdf.IntImage GetImage( byte[] data ) 
  {
    Stream isp = new MemoryStream( data );
    Reader r = new Reader( isp );
    return r.GetImage();
  }

  public static byte[] PNGID = { 137, 80, 78, 71, 13, 10, 26, 10 }; // PNG files always start with these bytes.

  Stream Inp;  // Input stream
  Stream DataStream; // For DecodePass
  MemoryStream Idat = new MemoryStream(); // Stream for IDAT

  int Width, Height, BitDepth, ColorType, CompressionMethod, FilterMethod, InterlaceMethod; // From IHDR chunk

  byte[] ColorTable; // From PLTE chunk

  bool GenBWMask, PalShades, HasCHRM = false;

  byte[] Image, Smask, Trans; // Transparency info from tRNS chunk
  int TransRedGray = -1, TransGreen = -1, TransBlue = -1; // Also from tRNS chunk

  int BytesPerPixel;
  int Channels;
  static byte [] ChannelsFromColorType = { 1, 0, 3, 1, 2, 0, 4 }; 

  float Gamma = 1f;
  float xW, yW, xR, yR, xG, yG, xB, yB;

  int DpiX, DpiY; float XYRatio; // From pHYs chunk

  Dict Additional = new Dict();
  DictName Intent; // from sRGB chunk
  ICC_Profile ICCP; // from iCCP chunk, ICC = International Color Consortium

  Reader( Stream inp ){ Inp = inp; }

  static void Assert( bool x ){ if ( !x ) throw new Exception();  }

  Pdf.IntImage GetImage()
  {
    ReadPng();
    CheckIccProfile();
    ProcessTrans();

    Channels = ChannelsFromColorType[ ColorType ];

    bool needDecode = InterlaceMethod == 1 || BitDepth == 16 || ( ColorType & 4 ) != 0 || PalShades || GenBWMask;

    if ( needDecode ) DecodeIdat(); else Image = Idat.ToArray();

    int components = Channels; if ( ( ColorType & 4 ) != 0 ) components -= 1;
  
    int bpc = BitDepth == 16 ? 8 : BitDepth;

    // Create the PdfImage
    Pdf.IntImage result = new Pdf.IntImage( Width, Height, components, bpc, Image );

    if ( !needDecode )
    {
      result.Deflated = true;
      Dict decodeparms = new Dict();
      decodeparms.Put( DictName.BitsPerComponent,BitDepth );
      decodeparms.Put( DictName.Predictor, 15 );
      decodeparms.Put( DictName.Columns, Width );
      decodeparms.Put( DictName.Colors, ( ColorType == 3 || ( ColorType & 2 ) == 0 ) ? 1 : 3 );
      Additional.Put( DictName.DecodeParms, decodeparms );
    }
    if ( Additional.Get( DictName.ColorSpace ) == null ) Additional.Put( DictName.ColorSpace, GetColorspace() );
    if ( Intent != null ) Additional.Put( DictName.Intent, Intent );
    if ( Additional.D.Count > 0 ) result.Additional = Additional;

    if ( ICCP != null ) result.ICCP = ICCP;

    if ( PalShades || GenBWMask )
    {
      IntImage m = new IntImage( Width, Height, 1, GenBWMask ? 1 : 8, Smask ); 
      m.IsMask = true; 
      result.ImageMask = m;
    }

    result.SetDpi( DpiX, DpiY );
    result.XYRatio = XYRatio;
    return result;
  }

  // Chunk types : IHDR, PLTE, IDAT, IEND, tRNS, pHYs, gAMA, cHRM, sRGB, iCCP
  const uint PNG0 = 0x89504E47U, PNG1 = 0x0D0A1A0AU,
    IHDR = 0x49484452U, PLTE = 0x504C5445U, IDAT = 0x49444154U, IEND = 0x49454E44U, tRNS = 0x74524E53U,
    pHYs = 0x70485973U, gAMA = 0x67414D41U, cHRM = 0x6348524EU, sRGB = 0x73524742U, iCCP = 0x69434350U;

  static DictName[] Intents = {DictName.Perceptual,DictName.RelativeColorimetric,DictName.Saturation,DictName.AbsoluteColorimetric};

  void ReadPng()
  {
    for ( int i = 0; i < PNGID.Length; i++ ) Assert( PNGID[ i ] == Inp.ReadByte() );
    byte[] buffer = new byte[ 1024 ];
    while ( true )
    {
      int len = GetInt( Inp );
      uint chunktype = GetUInt( Inp );
      Assert( len >= 0 );
      if ( chunktype == IHDR ) // header
      {
        Width = GetInt( Inp );
        Height = GetInt( Inp );

        BitDepth = Inp.ReadByte();
        ColorType = Inp.ReadByte();
        CompressionMethod = Inp.ReadByte();
        FilterMethod = Inp.ReadByte();
        InterlaceMethod = Inp.ReadByte();
      }
      else if ( chunktype == IDAT ) // the image data
      {
        Util.Copy( Inp, Idat, len, buffer );
      }
      else if ( chunktype == tRNS )  // transparency information
      {
        switch ( ColorType )
        {
          case 0:
            if ( len >= 2 )
            {
              len -= 2;
              int gray = GetWord( Inp );
              if ( BitDepth == 16 )
                TransRedGray = gray;
              else
                Additional.Put( DictName.Mask, "[ " + gray + " " + gray + " ]" );
            }
            break;
          case 2:
            if ( len >= 6 )
            {
              len -= 6;
              int red = GetWord( Inp );
              int green = GetWord( Inp );
              int blue = GetWord( Inp );
              if ( BitDepth == 16 )
              {
                TransRedGray = red;
                TransGreen = green;
                TransBlue = blue;
              }
              else
                Additional.Put( DictName.Mask, "[ " + red + " " + red + " " + green + " " + green + " " + blue + " " + blue + " ]" );
            }
            break;
          case 3:
            if ( len > 0 )
            {
              Trans = new byte[ len ];
              for ( int k = 0; k < len; ++k )
                Trans[ k ] = ( byte )Inp.ReadByte();
              len = 0;
            }
            break;
        }
        Util.Skip( Inp, len );
      }
      else if ( chunktype == PLTE ) // contains the palette; list of colors.
      {
        if ( ColorType == 3 )
        {
          DictArray colorspace = new DictArray();
          colorspace.Add( DictName.Indexed );
          colorspace.Add( GetColorspace() );
          colorspace.Add( len / 3 - 1 );
          ColorTable = new byte[ len ]; 
          int ix = 0; while ( ( len-- ) > 0 ) ColorTable[ ix++ ] = ( byte )Inp.ReadByte();
          colorspace.Add( new PdfByteStr( ColorTable ) );
          Additional.Put( DictName.ColorSpace, colorspace );
        }
        else Util.Skip( Inp, len );    
      }
      else if ( chunktype == pHYs ) // Currently nothing is done with this info.
      {
        int dx = GetInt( Inp );
        int dy = GetInt( Inp );
        int unit = Inp.ReadByte();
        if ( unit == 1 )
        {
          DpiX = ( int )( ( float )dx * 0.0254f + 0.5f );
          DpiY = ( int )( ( float )dy * 0.0254f + 0.5f );
        }
        else
        {
          if ( dy != 0 ) XYRatio = ( float )dx / ( float )dy;
        }
      }
      else if ( chunktype == cHRM )  // gives the chromaticity coordinates of the display primaries and white point.
      {
        xW = ( float )GetInt( Inp ) / 100000f;
        yW = ( float )GetInt( Inp ) / 100000f;
        xR = ( float )GetInt( Inp ) / 100000f;
        yR = ( float )GetInt( Inp ) / 100000f;
        xG = ( float )GetInt( Inp ) / 100000f;
        yG = ( float )GetInt( Inp ) / 100000f;
        xB = ( float )GetInt( Inp ) / 100000f;
        yB = ( float )GetInt( Inp ) / 100000f;
        HasCHRM = !( Math.Abs( xW ) < 0.0001f || Math.Abs( yW ) < 0.0001f || Math.Abs( xR ) < 0.0001f || Math.Abs( yR ) < 0.0001f 
          || Math.Abs( xG ) < 0.0001f || Math.Abs( yG ) < 0.0001f || Math.Abs( xB ) < 0.0001f || Math.Abs( yB ) < 0.0001f );
      }
      else if ( chunktype == sRGB ) // indicates that the standard sRGB color space is used.
      {
        int ri = Inp.ReadByte();
        Intent = Intents[ ri ];
        Gamma = 2.2f;
        xW = 0.3127f; yW = 0.329f; xR = 0.64f; yR = 0.33f;
        xG = 0.3f;    yG = 0.6f;   xB = 0.15f; yB = 0.06f;
        HasCHRM = true;
      }
      else if ( chunktype == gAMA )
      {
        int gm = GetInt( Inp );
        if ( gm != 0 )
        {
          Gamma = 100000f / ( float )gm;
          if ( !HasCHRM )
          {
            xW = 0.3127f; yW = 0.329f; xR = 0.64f; yR = 0.33f;
            xG = 0.3f;    yG = 0.6f;   xB = 0.15f; yB = 0.06f;
            HasCHRM = true;
          }
        }
      }
      else if ( chunktype == iCCP )
      {
        // Console.WriteLine( "iCCP chunk found" );
        do { len -= 1; } while ( Inp.ReadByte() != 0 );
        Inp.ReadByte(); len -= 1;
        byte[] icc = new byte[ len ];
        Util.ReadN( Inp, icc, 0, len );
        icc = Util.Inflate( icc );
        ICCP = ICC_Profile.GetInstance( icc );
      }
      else if ( chunktype == IEND ) break;
      else Util.Skip( Inp, len );      
      Util.Skip( Inp, 4 );
    }
  }

  void ProcessTrans()
  {
    int pal0 = 0;
    int palIdx = 0;
    PalShades = false;
    if ( Trans != null )
    {
      for ( int k = 0; k < Trans.Length; ++k )
      {
        int tk = Trans[ k ];
        if ( tk == 0 ) { pal0  += 1; palIdx = k; }
        if ( tk != 0 && tk != 255 ) { PalShades = true; break; }
      }
    }
    if ( ( ColorType & 4 ) != 0 ) PalShades = true;
    GenBWMask = ( !PalShades && ( pal0 > 1 || TransRedGray >= 0 ) );
    if ( !PalShades && !GenBWMask && pal0 == 1 ) Additional.Put( DictName.Mask, "[ " + palIdx + " " + palIdx + " ]" );
  }

  DictElem GetColorspace()
  {
    if ( ICCP != null || ( Gamma == 1f && !HasCHRM ) )
      return ( ColorType & 2 ) == 0 ? DictName.DeviceGray : DictName.DeviceRGB;
    else
    {
      DictArray array = new DictArray();
      Dict dic = new Dict();
      if ( ( ColorType & 2 ) == 0 )
      {
        if ( Gamma == 1f ) return DictName.DeviceGray;
        array.Add( DictName.CalGray );
        dic.Put( DictName.Gamma, Gamma );
        dic.Put( DictName.WhitePoint, "[ 1 1 1 ]" );
        array.Add( dic );
      }
      else
      {
        DictArray wp = new DictArray( new float[] { 1,1,1 } );
        array.Add( DictName.CalRGB );
        if ( Gamma != 1f )
        {
          DictArray gm = new DictArray( new float[] { Gamma, Gamma, Gamma } );
          dic.Put( DictName.Gamma, gm );
        }
        if ( HasCHRM )
        {
          float z = yW * ( ( xG - xB ) * yR - ( xR - xB ) * yG + ( xR - xG ) * yB );
          float YA = yR * ( ( xG - xB ) * yW - ( xW - xB ) * yG + ( xW - xG ) * yB ) / z;
          float XA = YA * xR / yR;
          float ZA = YA * ( ( 1 - xR ) / yR - 1 );
          float YB = -yG * ( ( xR - xB ) * yW - ( xW - xB ) * yR + ( xW - xR ) * yB ) / z;
          float XB = YB * xG / yG;
          float ZB = YB * ( ( 1 - xG ) / yG - 1 );
          float YC = yB * ( ( xR - xG ) * yW - ( xW - xG ) * yW + ( xW - xR ) * yG ) / z;
          float XC = YC * xB / yB;
          float ZC = YC * ( ( 1 - xB ) / yB - 1 );
          float XW = XA + XB + XC;
          float YW = 1;//YA+YB+YC;
          float ZW = ZA + ZB + ZC;
          wp = new DictArray( new float[]{ XW, YW, ZW } );
          DictArray matrix = new DictArray( new float[]{ XA, YA, ZA, XB, YB, ZB, XC, YC, ZC } );
          dic.Put( DictName.Matrix, matrix );
        }
        dic.Put( DictName.WhitePoint, wp );
        array.Add( dic );
      }
      return array;
    }
  }

  void CheckIccProfile()
  {
    int expected = ColorType == 0 || ColorType == 4 ? 1 : 3;
    if ( ICCP != null && ICCP.NumComponents != expected ) ICCP = null;
  }

  // Rest is decoding Idat data.

  void DecodeIdat()
  {
    int nbitDepth = BitDepth;
    if ( nbitDepth == 16 ) nbitDepth = 8;
    int size = -1;
    BytesPerPixel = ( BitDepth == 16 ) ? 2 : 1;
    switch ( ColorType )
    {
      case 0: size = ( nbitDepth * Width + 7 ) / 8 * Height; break;
      case 2: size = Width * 3 * Height;  BytesPerPixel *= 3; break;
      case 3:
        if ( InterlaceMethod == 1 ) size = ( nbitDepth * Width + 7 ) / 8 * Height;
        BytesPerPixel = 1;
        break;
      case 4: size = Width * Height; BytesPerPixel *= 2; break;
      case 6: size = Width * 3 * Height; BytesPerPixel *= 4; break;
    }
    if ( size >= 0 ) Image = new byte[ size ];
    if ( PalShades ) Smask = new byte[ Width * Height ];
    else if ( GenBWMask ) Smask = new byte[ ( Width + 7 ) / 8 * Height ];
    // Idat.Position = 0; DataStream = new Zlib.ZInflaterInputStream( Idat );
    DataStream = new MemoryStream( (new Inflator()).Go( Idat.ToArray() ).ToArray() );
    if ( InterlaceMethod != 1 ) 
      DecodePass( 0, 0, 1, 1, Width, Height );    
    else // Adam7 interlaced ( to allow image to be partially displayed before it is fully transmitted )
    {
      DecodePass( 0, 0, 8, 8, ( Width + 7 ) / 8, ( Height + 7 ) / 8 );
      DecodePass( 4, 0, 8, 8, ( Width + 3 ) / 8, ( Height + 7 ) / 8 );
      DecodePass( 0, 4, 4, 8, ( Width + 3 ) / 4, ( Height + 3 ) / 8 );
      DecodePass( 2, 0, 4, 4, ( Width + 1 ) / 4, ( Height + 3 ) / 4 );
      DecodePass( 0, 2, 2, 4, ( Width + 1 ) / 2, ( Height + 1 ) / 4 );
      DecodePass( 1, 0, 2, 2, Width / 2, ( Height + 1 ) / 2 );
      DecodePass( 0, 1, 1, 2, Width, Height / 2 );
    }
    DataStream.Close();
  }

  void DecodePass( int xOffset, int yOffset, int xStep, int yStep, int passWidth, int passHeight )
  {
    if ( passWidth == 0 || passHeight == 0 ) return;

    int bytesPerRow = ( Channels * passWidth * BitDepth + 7 ) / 8;
    byte[] curr = new byte[ bytesPerRow ];
    byte[] prior = new byte[ bytesPerRow ];

    for ( int srcY = 0, dstY = yOffset; srcY < passHeight; srcY  += 1, dstY  += yStep )
    {
      int filter = DataStream.ReadByte();
      Util.ReadN( DataStream, curr, 0, bytesPerRow ); 
      switch ( filter )
      {
        case 0: break;
        case 1: SubFilter( curr, bytesPerRow, BytesPerPixel ); break;
        case 2: UpFilter( curr, prior, bytesPerRow ); break;
        case 3: AverageFilter( curr, prior, bytesPerRow, BytesPerPixel ); break;
        case 4: PaethFilter( curr, prior, bytesPerRow, BytesPerPixel ); break;
      }
      SetPixels( curr, xOffset, xStep, dstY, passWidth );
      byte[] tmp = prior; prior = curr; curr = tmp;
    }
  }

  void SetPixels( byte[] curr, int xOffset, int step, int y, int width )
  {
    int srcX, dstX;

    int[] outp = GetPixel( curr );
    int sizes = 0;
    switch ( ColorType )
    {
      case 0: case 3: case 4: sizes = 1; break;
      case 2: case 6: sizes = 3; break;
    }
    if ( Image != null )
    {
      dstX = xOffset;
      int yStride = ( sizes * this.Width * ( BitDepth == 16 ? 8 : BitDepth ) + 7 ) / 8;
      for ( srcX = 0; srcX < width; srcX++ )
      {
        SetPixel( Image, outp, Channels * srcX, sizes, dstX, y, BitDepth, yStride );
        dstX  += step;
      }
    }
    if ( PalShades )
    {
      if ( ( ColorType & 4 ) != 0 ) 
      {
        if ( BitDepth == 16 )
        {
          for ( int k = 0; k < width; ++k )
          {
            int t = k * Channels + sizes;
            outp[ t ] = unchecked( (int)( (uint)outp[ t ] >> 8 ) );
          }
        }
        int yStride = this.Width;
        dstX = xOffset;
        for ( srcX = 0; srcX < width; srcX++ )
        {
          SetPixel( Smask, outp, Channels * srcX + sizes, 1, dstX, y, 8, yStride );
          dstX  += step;
        }
      }
      else
      { // colorType 3
        int yStride = this.Width;
        int[] v = new int[ 1 ];
        dstX = xOffset;
        for ( srcX = 0; srcX < width; srcX++ )
        {
          int idx = outp[ srcX ];
          v[0] = idx < Trans.Length ? (int)Trans[ idx ] : 255;
          SetPixel( Smask, v, 0, 1, dstX, y, 8, yStride );
          dstX  += step;
        }
      }
    }
    else if ( GenBWMask )
    {
      switch ( ColorType )
      {
        case 3:
          {
            int yStride = ( this.Width + 7 ) / 8;
            int[] v = new int[ 1 ];
            dstX = xOffset;
            for ( srcX = 0; srcX < width; srcX++ )
            {
              int idx = outp[ srcX ];
              v[ 0 ] = ( ( idx < Trans.Length && Trans[ idx ] == 0 ) ? 1 : 0 );
              SetPixel( Smask, v, 0, 1, dstX, y, 1, yStride );
              dstX  += step;
            }
            break;
          }
        case 0:
          {
            int yStride = ( this.Width + 7 ) / 8;
            int[] v = new int[ 1 ];
            dstX = xOffset;
            for ( srcX = 0; srcX < width; srcX++ )
            {
              int g = outp[ srcX ];
              v[ 0 ] = ( g == TransRedGray ? 1 : 0 );
              SetPixel( Smask, v, 0, 1, dstX, y, 1, yStride );
              dstX  += step;
            }
            break;
          }
        case 2:
          {
            int yStride = ( this.Width + 7 ) / 8;
            int[] v = new int[ 1 ];
            dstX = xOffset;
            for ( srcX = 0; srcX < width; srcX++ )
            {
              int markRed = Channels * srcX;
              v[ 0 ] = ( outp[ markRed ] == TransRedGray && outp[ markRed + 1 ] == TransGreen
                  && outp[ markRed + 2 ] == TransBlue ? 1 : 0 );
              SetPixel( Smask, v, 0, 1, dstX, y, 1, yStride );
              dstX  += step;
            }
            break;
          }
      }
    }
  }

  int[] GetPixel( byte[] curr )
  {
    switch ( BitDepth )
    {
      case 8:
        {
          int[] outp = new int[ curr.Length ];
          for ( int k = 0; k < outp.Length; ++k )
            outp[ k ] = curr[ k ];
          return outp;
        }
      case 16:
        {
          int[] outp = new int[ curr.Length / 2 ];
          for ( int k = 0; k < outp.Length; ++k )
            outp[ k ] = ( curr[ k * 2 ] << 8 ) + curr[ k * 2 + 1 ];
          return outp;
        }
      default:
        {
          int[] outp = new int[ curr.Length * 8 / BitDepth ];
          int idx = 0;
          int passes = 8 / BitDepth;
          int mask = ( 1 << BitDepth ) - 1;
          for ( int k = 0; k < curr.Length; ++k )
          {
            for ( int j = passes - 1; j >= 0; --j )
            {
              outp[ idx++ ] = unchecked( (int)( (uint)curr[ k ] >> BitDepth * j ) ) & mask;
            }
          }
          return outp;
        }
    }
  }

  // Remainder is static functions.

  /*
  static int UnsignedShiftRight( int value, int nbits )
  {        
    return unchecked( ( int )( ( uint )value >> nbits ) );
  }
  */

  static int GetPixel( byte[] image, int x, int y, int bitDepth, int bytesPerRow )
  {
    if ( bitDepth == 8 )
    {
      int pos = bytesPerRow * y + x;
      return image[ pos ] & 0xff;
    }
    else
    {
      int pos = bytesPerRow * y + x / ( 8 / bitDepth );
      int v = image[ pos ] >> ( 8 - bitDepth * ( x % ( 8 / bitDepth ) ) - bitDepth );
      return v & ( ( 1 << bitDepth ) - 1 );
    }
  }

  static void SetPixel( byte[] image, int[] data, int offset, int size, int x, int y, int bitDepth, int bytesPerRow )
  {
    if ( bitDepth == 8 )
    {
      int pos = bytesPerRow * y + size * x;
      for ( int k = 0; k < size; ++k ) image[ pos + k ] = ( byte )data[ k + offset ];
    }
    else if ( bitDepth == 16 )
    {
      int pos = bytesPerRow * y + size * x;
      for ( int k = 0; k < size; ++k ) image[ pos + k ] = ( byte )( data[ k + offset ] >> 8 );
    }
    else
    {
      int pos = bytesPerRow * y + x / ( 8 / bitDepth );
      int v = data[ offset ] << ( 8 - bitDepth * ( x % ( 8 / bitDepth ) ) - bitDepth );
      image[ pos ] |= ( byte )v;
    }
  }

  static void SubFilter( byte[] curr, int count, int bpp )
  {
    for ( int i = bpp; i < count; i += 1 )
      curr[ i ] = unchecked( ( byte )( curr[ i ] + curr[ i - bpp ] ) );
  }

  static void UpFilter( byte[] curr, byte[] prev, int count )
  {
    for ( int i = 0; i < count; i += 1 )
      curr[ i ] = unchecked( ( byte )( curr[ i ] + prev[ i ] ) );
  }

  static void AverageFilter( byte[] curr, byte[] prev, int count, int bpp )
  {
    unchecked
    {
      for ( int i = 0; i < bpp; i  += 1 )
        curr[ i ] = ( byte )( curr[ i ] + prev[ i ] / 2 );

      for ( int i = bpp; i < count; i  += 1 )
        curr[ i ] = ( byte )( curr[ i ] + ( curr[ i - bpp ] + prev[ i ] ) / 2 );
    }
  }

  static int PaethPredictor( int a, int b, int c )
  {
    int p = a + b - c;
    int pa = Math.Abs( p - a );
    int pb = Math.Abs( p - b );
    int pc = Math.Abs( p - c );

    if ( ( pa <= pb ) && ( pa <= pc ) ) return a;    
    else if ( pb <= pc ) return b;
    else return c;
  }

  static void PaethFilter( byte[] curr, byte[] prev, int count, int bpp )
  {
    unchecked 
    {
      for ( int i = 0; i < bpp; i += 1 )
        curr[ i ] = ( byte )( curr[ i ] + prev[ i ] );

      for ( int i = bpp; i < count; i += 1 )
        curr[ i ] = ( byte )( curr[ i ] + PaethPredictor( curr[ i - bpp ], prev[ i ], prev[ i - bpp ] ) );
    }
  }

  static int GetInt( Stream isp )
  {
    return ( isp.ReadByte() << 24 ) + ( isp.ReadByte() << 16 ) + ( isp.ReadByte() << 8 ) + isp.ReadByte();
  }

  static uint GetUInt( Stream isp )
  {
    uint result = 0;
    for ( int i=0; i < 4; i  += 1 ) result = ( result << 8 ) + ( byte )isp.ReadByte();
    return result;
  }

  static int GetWord( Stream isp )
  {
    return ( isp.ReadByte() << 8 ) + isp.ReadByte();
  }

} // PngImage

} // namespace
