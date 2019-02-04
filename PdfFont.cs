using String = System.String; 
using Generic = System.Collections.Generic; 

namespace Pdf
{

// Top level classes in this file : DynObj, PdfFont, StandardFont, FontFamily, StandardFontFamily.

// DynObj allows for objects, such as a font subset, which cannot be written to the PDF until finalisation.
public abstract class DynObj
{
  public int Obj; // Obj number in PDF file
  public virtual void GetObj( PdfWriter w ){ w.AllocDynObj(this); }
  public virtual void WriteTo( PdfWriter w ){}
}

public abstract class PdfFont : DynObj
{
  public String Name; 
  public abstract float Width( int c, int fontsize );
  public abstract void Encode( string s, int start, int end, Generic.List<byte> buf );
}

// PDF has a limited number of built-in standard fonts.
public class StandardFont : PdfFont
{
  public StandardFont Symbol() { return new StandardFont( "Symbol", PdfMetric.Symbol ); }
  // Zapf dingbats not currently supported.

  short [] CharWidth; // List of character widths ( unit is 1/1000 of fontsize ).

  public StandardFont( String name, short [] cw ){ Name = name; CharWidth = cw; }
  
  public override void GetObj( PdfWriter w )
  {
    if ( Obj == 0 ) 
    {
      Obj = w.StartObj();
      w.Put("<</Type/Font/Subtype/Type1/Name/F" + Obj + "/BaseFont/" + Name + "/Encoding/WinAnsiEncoding>>");
      w.EndObj();
    }
  }

  public override float Width( int c, int fontsize ) // Used for line wrapping calculation.
  {
    if ( c < 32 ) return 0;
    if ( CharWidth == null ) return 0.6f * fontsize; // Courier   
    int ix = (int)c - 32;
    float w = ix < CharWidth.Length ? CharWidth[ ix ] : 1000f;
    return w * 0.001f * fontsize;
  }   

  private byte [] EncBuffer = new byte[512];

  public override void Encode( string s, int start, int end, Generic.List<byte> buf )
  {
    System.Text.Encoding enc = System.Text.Encoding.GetEncoding(1252); // Not sure if this is right.
    int len = end-start;
    int need = enc.GetMaxByteCount( len );
    if ( need > EncBuffer.Length ) EncBuffer = Util.GetBuf( need );
    int nb = enc.GetBytes( s, start, len, EncBuffer, 0 );
    for ( int i = 0; i < nb; i += 1 ) 
    {
      byte b = EncBuffer[i];
      if ( b != 10 ) buf.Add( b );
    }
  }    
} // class StandardFont

public class FontFamily : Generic.List<PdfFont> { } // 0 = normal, 1 = bold, 2 = italic/oblique, 3 = bold+italic.

class StandardFontFamily : FontFamily
{
  StandardFontFamily( short [][] widths, String a, String b, String c, String d )
  { 
    Add(new StandardFont(a,widths[0])); 
    Add(new StandardFont(b,widths[1])); 
    Add(new StandardFont(c,widths[2])); 
    Add(new StandardFont(d,widths[3]));
  }

  public static FontFamily Courier() { return new StandardFontFamily( PdfMetric.CourierFamily, "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique" ); }
  public static FontFamily Times() { return new StandardFontFamily( PdfMetric.TimesFamily , "Times-Roman","Times-Bold", "Times-Italic",  "Times-BoldItalic" ); }
  public static FontFamily Helvetica() { return new StandardFontFamily( PdfMetric.HelveticaFamily, "Helvetica", "Helvetica-Bold","Helvetica-Oblique", "Helvetica-BoldOblique" ); }
} // class StandardFontFamily

} // namespace
