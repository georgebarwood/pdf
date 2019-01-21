// Extra bits for implementing embedded fonts / images. 

using String = System.String;
using IO = System.IO;
using Generic = System.Collections.Generic;

namespace Pdf {

public class ImageUtil
{
  // Adds an image to the PDF, returns info about the image. Use PdfWriter.CP.DrawImage to draw the Image on a page.
  public static PdfImage Add ( PdfWriter w, byte[] b  ) 
  {
    // System.Console.WriteLine( "Reading PNG size=" + b.Length );
    IntImage x = Pdf.Png.Reader.GetImage(b); // Only handles PNG format currently.
    // System.Console.WriteLine( "Got PNG" );
    PdfImage result = new PdfImage( x.Width, x.Height, IntImage.Write( x, w ) );
    // System.Console.WriteLine( "Wrote PNG" );
    return result;
  }
}

// Various low level classes for implementation of Pdf.Writer.

// IntImage is intermediate representation of an image.
// Dict, DictArray, DictName, DictNumber correspond to the structures in Pdf object dictionaries.
// PdfCmap writes a PDF font cmap.
// Util has house-keeping functions ( file and stream handling, compression ).

public class IntImage
{
  public int Width, Height, Components, BitsPerComponent;
  public byte[] Data;
  public bool Deflated = false; // Data has already been deflated.

  public Dict Additional; // Additional info about the image data, such as encoding and color.
  public ICC_Profile ICCP; // Not yet fully implemented, needs testing.

  public IntImage ImageMask;   // Some images have a mask ( "transparent" pixels ).
  public bool IsMask = false;  // This image is a mask.

  public float XYRatio; // e.g. from PNG pHYs chunk
  public void SetDpi( int x, int y ) { } // e.g. from PNG pHYs chunk

  public IntImage( int w, int h, int nc, int bpc, byte[] d )
  { 
    Width = w; Height = h; Components = nc; 
    BitsPerComponent = bpc; Data = d; 
  }

  public static int Write( IntImage x, PdfWriter w )
  {
    if ( x == null ) return 0;
    int maskObj = Write( x.ImageMask, w );

    byte [] content = x.Data; 
    if ( !x.Deflated ) 
    {
      content = PdfWriter.Deflate( content );
    }

    int result = w.StartObj();
    w.Put ( "<</Type/XObject/Subtype/Image"
        + "/Width " + x.Width + "/Height " + x.Height 
        + ( maskObj != 0 ? "/SMask " + maskObj + " 0 R" : "" )
        + "/Length " + content.Length
        + ( x.IsMask ? "/ColorSpace/DeviceGray" : "" ) 
        );
      
    if ( x.Additional != null ) x.Additional.OutputInner( w );
    w.Put ( "/BitsPerComponent " + x.BitsPerComponent + "/Filter/FlateDecode>>stream\n");
    w.Put( content ); w.Put( "\nendstream" ); w.EndObj();
    return result;
  }  
}  // End class IntImage

public class DictElem
{ public virtual void Output( PdfWriter w ){ w.Put( ToString() ); } }

public class Dict : DictElem
{
  public Generic.Dictionary<String,DictElem> D = new Generic.Dictionary<String,DictElem>();
  public override void Output( PdfWriter w ) { w.Put( "<<" ); OutputInner( w ); w.Put( ">>" ); }
  public void OutputInner( PdfWriter w )
  { foreach ( Generic.KeyValuePair<String,DictElem> p in D ) { w.Put( "/" + p.Key ); p.Value.Output( w ); } }
  public void Put( DictName Name, String value ) { D.Add( Name.Name, new DictName( value ) ); }
  public void Put( DictName Name, float value ) { D.Add( Name.Name, new DictNumber( value ) ); }
  public void Put( DictName Name, DictElem value ) { D.Add( Name.Name, value ); }
  public DictElem Get( DictName Name ) { if ( D.ContainsKey( Name.Name ) ) return D[Name.Name]; else return null; }
} // End class Dict

public class DictArray : DictElem
{
  Generic.List<DictElem> A = new Generic.List<DictElem>();
  public DictArray(){}
  public DictArray( float[] values ) { foreach( float f in values ) A.Add( new DictNumber( f ) ); }
  public void Add( float n ) { A.Add( new DictNumber( n ) ); }
  public void Add( DictElem x ) { A.Add( x ); }
  public override void Output( PdfWriter w )
  { w.Put( "[" ); foreach ( DictElem p in A ) p.Output( w ); w.Put( "]" ); }
} // End class DictArray

public class DictName : DictElem
{
  public String Name;
  public DictName( String s ) { Name = s; }
  public override String ToString(){ return "/" + Name; } // Per PDF spec. section 7.3.5 "Name Objects".

  public static DictName 
    AbsoluteColorimetric = new DictName( "AbsoluteColorimetric" ),
    BitsPerComponent = new DictName( "BitsPerComponent" ),
    CalGray = new DictName( "CalGray" ),
    CalRGB = new DictName( "CalRGB" ),
    Colors = new DictName( "Colors" ),
    ColorSpace = new DictName( "ColorSpace" ),
    Columns = new DictName( "Columns" ),
    DecodeParms = new DictName( "DecodeParms" ),
    DeviceGray = new DictName( "DeviceGray" ),
    DeviceRGB = new DictName( "DeviceRGB" ),
    Gamma = new DictName( "Gamma" ),
    Indexed = new DictName( "Indexed" ),
    Intent = new DictName( "Intent" ), 
    Mask = new DictName( "Mask" ),
    Matrix = new DictName( "Matrix" ), 
    Predictor = new DictName( "Predictor" ),
    Perceptual = new DictName( "Perceptual" ),
    RelativeColorimetric = new DictName( "RelativeColorimetric" ),
    Saturation = new DictName( "Saturation" ),
    WhitePoint = new DictName( "WhitePoint" );
} // End class DictName

public class DictNumber : DictElem
{
  public float Value;
  public DictNumber( float v ){ Value = v; }
  public override String ToString(){ return " " + Value.ToString( "G5" ); }
} // End class DictNumber

public class PdfByteStr : DictElem // Used to represent PNG colortable data
{
  public byte [] Value;
  public PdfByteStr( byte [] v ){ Value = v; }
  public override void Output( PdfWriter w )
  {
    // Code is questionable, maybe simpler to use hex string format. Some byte values ( e.g. 13 ) may be misinterpreted ).
    w.Put( "( " );
    for ( int i=0; i<Value.Length; i += 1 ) w.PutStrByte( Value[i] );
    w.Put( " )" );
  }
} // End class PdfByteStr

public class ICC_Profile
{
  public int NumComponents;
  public static ICC_Profile GetInstance( byte[] input ) { return null; } // ToDo...
} // End class ICC_Profile

internal class PdfCmap : IO.MemoryStream // For writing Pdf Cmap to Pdf file.
{
  public static int Write( PdfWriter w, int [] codes ) // returns Pdf object number, codes is array of unicode code points.
  {
    return (new PdfCmap()).Go( w, codes );
  }

  private void Put( String s ){ for ( int i=0; i < s.Length; i += 1 ) WriteByte( (byte) s[i] ); }

  private int Go( PdfWriter w, int [] codes ) // codes is array of Unicode values.
  {
    Put( "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo <</Registry(Adobe)/Ordering(UCS)/Supplement 0>>" 
     + " def /CMapName /UC def /CMapType 2 def " );
    Put( "\n1 begincodespacerange <0000> <FFFF> endcodespacerange" );
    Put( "\n1 beginbfrange <0000> <" + (codes.Length-1).ToString("X4" ) + "> [");

    for ( int i=0; i < codes.Length; i += 1 ) 
    {
      uint u = (uint) codes[i];
      if ( u < 0xD800 || ( u >= 0xE000 && u <= 0xFFFF ) ) // Surrogate pair not needed.
      {
         Put( "<" + u.ToString("X4") + ">\n" );
      }
      else // Have to use UTF-16BE surrogate pair
      {
         u -= 0x10000;
         uint hi = 0xD800 + ( u >> 10 );
         uint lo = 0xDC00 + ( u & 0x3ff );        
         Put( "<" + hi.ToString("X4") + lo.ToString("X4") + ">\n" );
      }
    }
    Put( "]\nendbfrange endcmap CMapName currentdict /CMap defineresource pop end end" );

    return w.PutStream( ToArray() );
  }

} // end Class PdfCmap

} // Namespace

