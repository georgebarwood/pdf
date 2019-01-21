using String = System.String; 
using Char = System.Char; 
using IO = System.IO;
using Generic = System.Collections.Generic; 

namespace Pdf {

public class PdfImage
{
  public int Width, Height, Obj;
  public PdfImage( int w, int h, int o ) { Width = w; Height = h; Obj = o; }
}

public class PdfPage
{
  public int Number;
  public float Width, Height, MarginTop, MarginLeft, MarginRight, MarginBottom, X, Y;

  // Graphics operations

  public void Line( float x0, float y0, float x1, float y1 ) // Draw a line from (x0,y0) to (x1,y1)
  { Put("\n" + x0 + " " + y0 + " m " + x1 + " " + y1 + " l S");  }

  public void Rect( float x0, float y0, float x1, float y1 ) // Draw a rectange corners (x0,y0) to (x1,y1)
  { Put("\n" + x0 + " " + y0 + " " + x1 + " " + y1 + " re S");  }

  public void DrawImage( PdfImage I, float x, float y, float scale )
  {
    if ( I == null ) return;
    float w = I.Width * scale;
    float h = I.Height * scale;
    NoteXobj( I.Obj );
    Put( "\nq " + w.ToString("G5") + " 0 0 " + h.ToString("G5") + " " + x + " " + y + " cm /X" + I.Obj + " Do Q");
  }

  public void Put( String s )
  { for ( int i = 0; i < s.Length; i += 1 ) OS.WriteByte( (byte) s[i] ); }

  // Text functions.

  private Generic.List<byte> StrBuffer = new Generic.List<byte>(); // For accumulating a "Tj" string.

  public void Txt( String s, int start, int end )
  {
    if ( start >= end ) return;
    CurFont.Encode( s, start, end, StrBuffer );
  }

  public void Txt( String s ){ Txt( s, 0, s.Length ); } // Write string to Str Buffer ( for Tj )

  public void FlushStrBuffer()
  {
    if ( StrBuffer.Count == 0 ) return;

    if ( LastFont != CurFont || LastFontSize != FontSize )
    {
      Fonts.Add( CurFont.Obj );
      TSW( "/F" + CurFont.Obj + " " + FontSize + " Tf" );
      LastFont = CurFont;
      LastFontSize = FontSize;
    }

    bool useHex = false;
    foreach ( byte b in StrBuffer ) if ( b < 32 || b >= 128 ) { useHex = true; break; }

    if ( useHex )
    {
      TS.WriteByte( (byte)'<' );
      foreach ( byte b in StrBuffer )
      {
        int x = b>>4; x += x < 10 ? 48 : 55; TS.WriteByte( (byte)x );
        x = b & 15;  x += x < 10 ? 48 : 55; TS.WriteByte( (byte)x );
      }
      TSW( "> Tj" );
    }
    else
    {
      TS.WriteByte( (byte)'(' );
      foreach ( byte b in StrBuffer )
      {
        if ( b == (byte)'(' || b == (byte)')' || b == (byte) '\\' ) TS.WriteByte( (byte) '\\' );
        TS.WriteByte( b );
      }
      TSW( ") Tj" );
    }
    StrBuffer.Clear();
  }

  public void TSW( String s ) // Write directly to TS ( text stream )
  { for ( int i = 0; i < s.Length; i += 1 ) TS.WriteByte( (byte)s[i] );  }

  // Functions to update text style.

  private PdfFont CurFont = null, LastFont = null; 
  private int FontSize, LastFontSize, Super=0;
  private float CharSpacing=0;
  private String Color = null;
  public String Other = null;

  public void SetCharSpacing( float x )
  {
    if ( CharSpacing != x )
    {
      FlushStrBuffer();
      CharSpacing = x;
      TSW( " " + x + " Tc" );
    }
  }

  public void SetFont( PdfFont f, int fontSize )
  {
    if ( CurFont != f || FontSize != fontSize )
    {
      FlushStrBuffer();
      CurFont = f;
      FontSize = fontSize;
      // Delay the actual Tf operation until the next Tj, to eliminate redundant Tf operations.
    }
  }

  public void SetSuper( int super )
  { if ( Super != super ) { FlushStrBuffer(); Super = super; TSW( " " + super + " Ts" ); } }

  public void SetColor( String color )
  { if ( Color != color ) { FlushStrBuffer(); Color = color; TSW( " " + color + " rg" ); } }

  public void SetOther( String other )
  { FlushStrBuffer(); Other = other; TSW( " " + other ); }

  // Text positioning functions ( newline ).

  public void Td(float x, float y) // Start a new line ( relative to previous line )
  {
    FlushStrBuffer();
    TSW( "\n" + x + " " + y + " Td " );
    X += x; Y += y;
  }

  public void Goto( float x, float y ) // Start a new line ( absolute position )
  { Td(x - X, y - Y); }

  // Functions for drawing a path.

  float Px, Py, Ps, Pcx, Pcy;
  public void PathInit( float x, float y, float s ){ Px = x; Py = y; Ps = s; }
  public void PathMove( float x, float y ){ x=Px+x*Ps; y=Py+y*Ps; Put( " " + x + " " + y + " m" ); Pcx=x; Pcy=y; }
  public void PathLine( float x, float y ){ x=Px+x*Ps; y=Py+y*Ps; Put( " " + x + " " + y + " l" ); Pcx=x; Pcy=y; }
  public void PathCurve( float x1, float y1, float x2, float y2 )
  {
    x1 = Px+x1*Ps; y1 = Py+y1*Ps; 
    x2 = Px+x2*Ps; y2 = Py+y2*Ps; // shift and scale
    float x3 = Pcx/3 + 2*x1/3;
    float y3 = Pcy/3 + 2*y1/3;
    float x4 = x2/3 + 2*x1/3;
    float y4 = y2/3 + 2*y1/3;
    Put( " " + x3 + " " + y3 + " " + x4 + " " + y4 + " " + x2 + " " + y2 + " c" ); // cubic bezier
    Pcx = x2; Pcy = y2;
  }
  public void PathFill(){ Put( " f"); }

  public void InitTxtFrom( PdfPage p )
  {
    if ( p == null ) return;
    if ( p.CurFont != null ) SetFont( p.CurFont, p.FontSize );
    if ( p.Super!=0 ) SetSuper( p.Super );
    if ( p.Color != null && p.Color != "0 0 0" ) SetColor( p.Color );
    if ( p.Other != null ) SetOther( p.Other );
  }

  public void ClearState()
  {
    FlushStrBuffer();
    X = 0; Y = 0; CurFont = null; LastFont = null;
    Super = 0; Color = null; Other = null;
  }

  // Writing pages to the PDF file.

  private System.IO.MemoryStream 
    OS = new System.IO.MemoryStream(), // Final output stream, includes graphics and text.
    TS = new System.IO.MemoryStream(); // Text stream.

  private readonly Generic.HashSet<int> Fonts = new Generic.HashSet<int>(); // Fonts used by page
  private readonly Generic.HashSet<int> Xobjs = new Generic.HashSet<int>(); // XObjects used by page ( typically images )

  private void NoteXobj( int objnum ){ Xobjs.Add(objnum); }

  private void EndText() // Copies TS to OS enclosed by "BT" and "ET".
  {
    FlushStrBuffer();
    Put( "\nBT" );
    byte [] b = TS.ToArray();
    OS.Write( b, 0, b.Length );
    Put( "\nET" );
  }   

  private static void PutResourceSet( PdfWriter w, Generic.HashSet<int> S, String n1, String n2 )
  {
    if ( S.Count > 0 ){ w.Put( n1 + "<<" ); foreach (int i in S) w.Put( n2 + i + " " + i + " 0 R" );  w.Put( ">>" );  }
  }

  public static int WritePages( PdfWriter w, Generic.List<PdfPage> pages )
  {
    System.Text.StringBuilder kids = new System.Text.StringBuilder();
    int pagesobj = w.AllocObj();

    foreach ( PdfPage p in pages )
    {
      w.CP = p;
      w.FinishPage();
      p.EndText();

      byte[] Content = p.OS.ToArray();
      int contentobj = w.PutStream( Content );

      int pageobj = w.StartObj();
      kids.Append( pageobj + " 0 R " );
      w.Put("<</Type/Page/Parent " + pagesobj
          + " 0 R/MediaBox[0 0 " + p.Width + " " + p.Height + "]/Contents " + contentobj
          + " 0 R/Resources <<");
      PutResourceSet( w, p.Fonts, "/Font", "/F" );
      PutResourceSet( w, p.Xobjs, "/XObject", "/X" );
      w.Put( " >> >>");
      w.EndObj();
    }
    w.StartObj( pagesobj ); w.Put("<</Type/Pages/Count " + pages.Count + "/Kids[" + kids + "]>>"); w.EndObj();
    return pagesobj;
  }

}  // End class PdfPage

} // namespace
