using String = System.String; 
using IO = System.IO;
using Generic = System.Collections.Generic; 

namespace Pdf {

public class PdfWriter // class for writing PDF ( Portable Document Format ) files ( https://en.wikipedia.org/wiki/PDF ) .
{
  // Example usage
  public static void Example1() 
  {
    using( IO.FileStream fs = IO.File.Create( "Example1.pdf") )
    {
      PdfWriter w = new Pdf.PdfWriter(); 
      w.Fonts = Pdf.StandardFontFamily.Times(); // Sets font family.
      w.Init( fs, "Hello World" ); // Sets output stream and document title.

      // Optional style settings.
      w.Justify = 2; // Causes text to be justified.
      w.SetColumns( 3, 5 ); // Sets pages to be formatted as 3 columns, 5pt space between columns.
      w.SetFont( w.Fonts[0], 10 ); // Sets font and font size.

      for ( int i = 0; i < 100; i += 1 ) w.Txt( "Some text which is long enough to demonstrate word wrapping. " );
      w.Finish();
    }
  }

  // Example with an image and embedded font ( subset ).
  public static void Example2() 
  {
    byte [] freeSansBytes = Util.GetFile( @"c:\PdfFiles\FreeSans.ttf" );
    byte [] myImageBytes = Util.GetFile( @"c:\PdfFiles\666.png" );

    using( IO.FileStream fs = IO.File.Create( "Example2.pdf") )
    {
      PdfWriter w = new Pdf.PdfWriter(); 
      w.Init( fs, "Hello World." );

      PdfImage myImage = ImageUtil.Add( w, myImageBytes ); 
      w.LineAdvance = myImage.Height / 2 + 10; // Make space for the image
      w.NewLine();
      w.LineAdvance = 15; // Restore LineAdvance to default value.
      w.CP.DrawImage( myImage, w.CP.X, w.CP.Y, 0.5f );

      PdfFont freeSans = new TrueTypeFont( "DJGTGD+Sans", freeSansBytes );
      w.SetFont( freeSans, 12 );
      w.Txt( "Hello world" );

      w.Finish();
    }
  }

  // PDF spec is at https://www.adobe.com/content/dam/acom/en/devnet/pdf/pdfs/PDF32000_2008.pdf
  // Required additional files: PdfPage.cs, Deflator.cs, PdfFont.cs, PdfMetric.cs.
  // Optional additional files: PdfTrueType.cs, TrueType.cs, PdfPng.cs, PDfOther.cs, Util.cs, Pdfwriter2.cs.

  public void Init( IO.Stream os, String title ) { OS = os; Title = title; Put( "%PDF-1.4\n" ); GetReadyToWrite(); }
  public String Title = "Untitled"; // Assign a string to set the PDF title.
  public void Txt( String s ) {  Txt( s,0,s.Length ); } // Write justified text, word-wrapping to new line or page as required.
  public void NewLine() { FlushWord(); FinishLine( false ); } // Force a new line.
  public void NewPage() { NewLine(); NewPage0(); } // Force a new page.
  
  // Page parameters
  public int PageWidth = 595, PageHeight = 842; // Units are pt, default is A4
  public int PageMarginLeft = 36, PageMarginRight = 36, PageMarginTop= 36, PageMarginBottom = 36;
  public PdfPage CP; // Current page, see PdfPage.cs for interface.

  // Line parameters ( in pt ) .
  public float LineLength = 523, LineAdvance=15, LineMarginBefore=0;

  // Text style parameters, updated by various functions such as SetFont, SetSuper etc.
  public FontFamily Fonts;
  public PdfFont CurFont; 
  public int FontSize=12, Super;

  // Line justification parameter.
  public int Justify=0; // Justify values are 0=right ragged, 1=center, 2=justifed.

  // Compression option
  public bool Compress = true; // Set to false to make PDF easier to examine when testing or if compression not wanted.

  // Functions to adjust text style.
  public void SetFont( PdfFont f, int fs ) { f.GetObj( this );  Word.Font( f, fs ); CurFont = f; FontSize = fs; }
  public void SetSuper( int x ) { Word.Super( x ); Super = x; }
  public void SetColor( String color ) { Word.Color( color ); }
  public void SetOther( String other ) { Word.Other( other ); }

  // Page and Newline functions.
  // The word buffer ( Word ) is flushed when word wrap occurs or a space is found.
  // The word buffer can represent multiple text style changes within the word.
  // The line buffer ( Line ) is written ( after justification ) to the current page when FinishLine is called.

  public virtual void StartPage() {} // Can be over-ridden to initialise the page ( e.g. set a background image, draw a border ) .
  public virtual void FinishPage() {} // Can be over-ridden to finalise the page ( e.g. write a page number ) .

  public void Finish() 
  {
    FlushWord();
    FinishLine( false );
    int pagesobj = PdfPage.WritePages( this, Pages );
    int catObj = PutObj( "<</Type/Catalog/Pages " + pagesobj + " 0 R>>" );
    int infoObj = PutInfo();
    foreach( DynObj x in DynObjs ) x.WriteTo( this );
    long startxref = OS_Total; int xc = Xref.Count+1;
    Put( "xref\n0 " + xc + "\n0000000000 65535 f\n" );
    for ( int i=0; i<Xref.Count; i += 1 ) Put( Xref[i].ToString( "D10" ) + " 00000 n\n" );
    Put( "trailer\n<</Size " + xc + "/Root " + catObj + " 0 R" + "/Info " + infoObj + " 0 R" + ">>\nstartxref\n" + startxref + "\n%%EOF\n" );
  }

  public void InitFont( PdfFont f ) { f.GetObj( this ); } // Only necessary if page font is set directly.

  public void SetColumns( int n, float colSpace )
  { 
    Columns = n; 
    ColSpace = colSpace;
    LineLength = ( ( PageWidth + colSpace - PageMarginRight - PageMarginLeft ) / n ) - colSpace;
  }

  public virtual void NewColumn()
  {
    if ( CurColumn+1 < Columns )
    {
      LineMarginBefore += LineLength + ColSpace;
      FirstLine = true;
      CurColumn += 1;
    }
    else
    {
      NewPage0();
    }
  }

  void NewPage0() // Start a new page ( without flushing word buffer, so current word can be carried over to next page ).
  { 
    PdfPage old=CP, p = new PdfPage();
    p.Height = PageHeight; p.Width = PageWidth; 
    p.MarginLeft = PageMarginLeft; p.MarginRight = PageMarginRight;
    p.MarginTop = PageMarginTop; p.MarginBottom = PageMarginBottom;
    Pages.Add( p ); p.Number = Pages.Count; CP = p; 
    FirstLine = true; LinePos = 0; SpaceCount = 0; LineCharCount = 0;
    CurColumn = 0; LineMarginBefore = 0;
    StartPage();
    CP.InitTxtFrom( old ); 
  }

  void FinishLine( bool wrap ) // Writes the line buffer to a page.
  {
    float space = LineLength - LinePos; if ( wrap && SpaceCount > 0 ) space += ( LinePos - SpacePos );
    float centerjustify = Justify == 1 ? space / 2 : 0; // Center justification
    int lineCharCount = LineCharCount;

    // GetSpace if needed.
    if ( !FirstLine && CP.Y - LineAdvance < CP.MarginBottom ) NewColumn();

    if ( FirstLine ) 
    { 
      CP.Goto( centerjustify + CP.MarginLeft + LineMarginBefore, CP.Height - CP.MarginTop - LineAdvance ); 
      FirstLine = false; 
    }
    else 
    {
      CP.Td( centerjustify + CP.MarginLeft + LineMarginBefore - CP.X, -LineAdvance );
    }
    CP.SetCharSpacing( wrap && Justify == 2 ? space / lineCharCount : 0 );
    Line.Flush( CP );
    LinePos = 0; SpaceCount = 0; LineCharCount = 0;
  }

  public void GetReadyToWrite() // Sets defaults if not provided by user.
  {  
    if ( CP == null ) 
    {
      if ( Fonts == null ) Fonts = StandardFontFamily.Helvetica();
      if ( CurFont == null ) SetFont( Fonts[0], FontSize );
      NewPage0();  
    }    
  }

  // Word and Line state, used to calculate line justification and word wrapping.
  float LinePos, SpacePos, ColSpace;
  int SpaceCount, WordCharCount, LineCharCount, Columns = 1, CurColumn = 0;
  bool FirstLine;

  // Streams, Lists and Buffers
  IO.Stream OS; // Final output stream.
  long OS_Total = 0; // Total bytes written to OS ( for xref table ) 
  protected Generic.List<PdfPage> Pages = new Generic.List<PdfPage>();
  Generic.List<DynObj> DynObjs = new Generic.List<DynObj>();
  Generic.List<long> Xref = new Generic.List<long>();
  WordBuffer Word = new WordBuffer();
  LineBuffer Line = new LineBuffer();

  void FlushWord() { Word.Flush( Line ); LineCharCount += WordCharCount; WordCharCount = 0; } 

  void WordAdd( String s, int start, int end ) // Append part of s to word buffer.
  { if ( start >= end ) return; Word.Str( s, start, end ); } 

  public void Txt( String s,int start, int end ) // Writes text word-wrapping to new line or page as required.
  {
    GetReadyToWrite();
    int i = start;
    while ( i < end ) 
    {
      char c = s[i]; int uc = System.Char.IsSurrogate( c ) ? char.ConvertToUtf32( s, i ) : c;

      float cwidth = CurFont.Width( uc, FontSize );
      if ( LinePos + cwidth > LineLength || c == '\n' ) 
      {
        float carry = 0; // Amount carried to next line.
        if ( SpaceCount > 0 && c != '\n' ) 
        {
          carry = LinePos - SpacePos; // Word wrap, current word is written to next line.
        }
        else
        {
          // No word wrap, flush the word buffer.
          WordAdd( s,start,i ); start = i; FlushWord();
        }     
        FinishLine( c != '\n' ); 
        LinePos = carry;
      }
      LinePos += cwidth;
      i += 1; 
      if ( c != '\n' ) WordCharCount += 1;
      if ( c == ' ' ) 
      {
        WordAdd( s,start,i ); start = i; FlushWord();
        SpacePos = LinePos; SpaceCount += 1;
      }
      else if ( System.Char.IsSurrogate( c ) ) i += 1; // Skip surrogate pair char
    }   
    WordAdd( s,start,end );
  }

  int PutInfo()
  {
    int result = StartObj();
    Put( "<</Title" );
    PutStr( Title );
    Put ( ">>" );
    EndObj();
    return result;
  }

  // Low level functions for PDF creation.

  public void Put( byte b ) { OS.WriteByte( b ); OS_Total += 1; }

  public void Put( byte [] b ) { OS.Write( b, 0, b.Length ); OS_Total += b.Length; }

  public void Put( string s ) 
  { for ( int i=0; i < s.Length; i += 1 ) OS.WriteByte( ( byte ) s[i] ); OS_Total += s.Length; }

  public void PutStrByte( byte b )
  { if ( b == '(' || b == '\\' || b == ')' )
      Put( ( byte ) '\\' );
    if ( b == 13 )
      Put( @"\015" ); // Octal escape must be used for 13, see spec, bottom of page 15.
    else
      Put( b );
  }

  static bool IsAscii( String s )
  {
    for ( int i = 0; i < s.Length; i += 1 ) if ( s[i] > 127 ) return false;
    return true;
  }

  public void PutStr( String s )
  {
    Put( (byte) '(' );
    if ( IsAscii (s) )
    {
      for ( int i=0; i < s.Length; i += 1 ) PutStrByte( (byte) s[i] );
    }
    else
    {
      Put( 254 ); Put( 255 ); // Byte order marker
      System.Text.Encoding enc = System.Text.Encoding.BigEndianUnicode;
      byte [] b = enc.GetBytes( s );
      for ( int i = 0; i < b.Length; i += 1 ) PutStrByte( b[i] );
    }
    Put( (byte) ')' );
  }

  public int AllocObj() { Xref.Add( 0 ); return Xref.Count; }

  public void AllocDynObj( DynObj x ) { if ( x.Obj == 0 ) { x.Obj = AllocObj();  DynObjs.Add( x ); } }

  public void StartObj( int ObjNum ) { Xref[ ObjNum-1 ] = OS_Total; Put( ObjNum + " 0 obj\n" ); }    

  public void EndObj() { Put( "\nendobj\n" ); }

  public int StartObj() { int obj = AllocObj(); StartObj( obj ); return obj; }

  public int PutObj( string s ) { int obj = StartObj(); Put( s ); EndObj(); return obj;  }

  // Compression functions

  public int PutStream( byte [] data ) 
  {
    int result = StartObj();
    Put( "<<" );
    if ( Compress ) 
    {
      Put( "/Filter/FlateDecode" );
      #if ( UseZLib ) 
      { 
        data = Deflate( data );
        Put( "/Length " + data.Length + ">>stream\n" );
        Put( data );
      } 
      #else
      {
        MemoryBitStream bb = new MemoryBitStream();
        Deflator.Deflate( data, bb, 1 );
        int clen = bb.ByteSize();
        Put( "/Length " + clen + ">>stream\n" );
        bb.CopyTo( OS );
        OS_Total += clen;
      }
      #endif
    }
    else 
    {
      Put( "/Length " + data.Length + ">>stream\n" );      
      Put( data );
    } 
    Put( "\nendstream" );
    EndObj(); 
    return result;   
  }

  public static byte [] Deflate( byte [] data ) 
  {
    #if ( UseZLib ) 
    IO.MemoryStream cs = new IO.MemoryStream();
    Zlib.ZDeflaterOutputStream zip = new Zlib.ZDeflaterOutputStream( cs );
    zip.Write( data, 0, data.Length ); 
    zip.Finish();
    return cs.ToArray();
    #else 
    MemoryBitStream bb = new MemoryBitStream();
    Deflator.Deflate( data, bb, 1 );
    return bb.ToArray();
    #endif
  }

} // End class PdfWriter

// WordBuffer and LineBuffer are used to implement text justification / line wrapping.

struct TextElem { public int Kind; public System.Object X; public int I1, I2; }

class WordBuffer : Generic.List<TextElem>
{
  public void Str( String x, int i1, int i2 ) { this.Add( new TextElem{ Kind=0, X = x, I1 = i1, I2 = i2 } ); }
  public void Font( PdfFont x, int i1 ) {  this.Add( new TextElem{ Kind=1, X = x, I1 = i1 } ); }
  public void Super( int i1 ) { this.Add( new TextElem{ Kind=2, I1 = i1 } ); }
  public void Color( String x ) { this.Add( new TextElem{ Kind=3, X = x } ); }
  public void Other( String x ) { this.Add( new TextElem{ Kind=4, X = x } ); }
  public void Flush( LineBuffer b ) { foreach ( TextElem e in this ) b.Add( e ); Clear(); }
}

class LineBuffer : Generic.List<TextElem>
{
  public void Flush( PdfPage p ) 
  {
    foreach ( TextElem e in this ) 
    {
      switch ( e.Kind ) 
      {
        case 0: p.Txt( ( String ) e.X, e.I1, e.I2 ); break;
        case 1: p.SetFont( ( PdfFont ) e.X, e.I1 ); break;
        case 2: p.SetSuper( e.I1 ); break;
        case 3: p.SetColor( ( String ) e.X ); break;
        case 4: p.SetOther( ( String ) e.X ); break;
      }
    }
    Clear();
  }
}

} // namespace
