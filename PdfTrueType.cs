using String = System.String;
using Generic = System.Collections.Generic;

namespace Pdf {

class TrueTypeFont : PdfFont // For embedding subset of a TrueType font in a PDF.
{
  // Typical usage:
  // TrueTypeFont f = new TrueTypeFont( "DJGTGD+FreeSans", Util.GetFile( @"C:\FreeSans.ttf" ) );
  // PdfWriter w = new PdfWriter();
  // w.SetFont( f, 12 ); 
  // Note: the font can only be used once per Pdf, but the byte array can be re-used. 

  public TrueTypeFont( String name, byte [] b ){ Name = name; Inp = new TrueType.Reader(b); } 

  public TrueType.Reader Inp;
  Generic.List<int> GList = new Generic.List<int>(); // List of used Glyph indexes.
  Generic.Dictionary<int,int> Xlat = new Generic.Dictionary<int,int>(); // Index of GList.
  Generic.Dictionary<int,int> Lookup = new Generic.Dictionary<int,int>(); // Cache of font cmap results ( also used to build ToUnicode map ).

  bool KeepInstructions = false; // Are font instructions retained ( not implemented, would need additional tables to be copied to PDF ).

  int XG( int gi ) // Translates a glyph index to the subset value.
  {
    if ( gi < 0 ) return gi;
    int x; if ( !Xlat.TryGetValue( gi, out x ) ) { x = GList.Count; Xlat[ gi ] = x; GList.Add( gi ); }
    return x;
  }

  int Index( int c ) // Get the glyph index for a unicode point, checks Lookup cache first.
  {
    int gi;
    if ( !Lookup.TryGetValue( c, out gi ) )
    {
      gi = Inp.FindGlyph(c);
      if ( gi < 0 ) return gi;
      Lookup[ c ] = gi; // cache result to avoid repeated calls to Inp.FindGlyph
    }
    return gi;
  }   

  // Encodes a string as a list of Glyph indexes.

  public override void Encode( string s, int start, int end, Generic.List<byte> buf )
  {
    for ( int i = start; i < end; i += 1 )
    {
      char c = s[i];
      if ( c != '\n' ) 
      { 
        int uc = c;
        if ( System.Char.IsSurrogate(c) ) { uc = char.ConvertToUtf32( s, i ); i += 1; }

        int gi = XG(Index( uc ));
        if ( gi >= 0 ) // If char not found, it is simply ignored ( in future may want a fallback mechanism ).
        {
          buf.Add( (byte)( gi >> 8 ) );
          buf.Add( (byte)( gi & 0xff ) );
        }
      }
    }
  }

  public override float Width( int uc, int fontsize ) // Used for line wrapping calculation. uc is a unicode point.
  {
    if ( uc == '\n' ) return 0;
    int gi = Index( uc );
    if ( gi < 0 ) return 0;
    TrueType.WidthInfo wi; Inp.GetGlyphWidth( gi, out wi );
    float x = ((float)wi.AdvanceWidth) / Inp.UnitsPerEm;
    return x * fontsize;
  }

  public int Unit( int a ){ return (int) ( ( a * 1000 ) / Inp.UnitsPerEm ); } // Maybe should use a float to avoid any loss of precision.

  public override void WriteTo( PdfWriter w )
  {
    int userglyph  = GList.Count; // GList.Count could increase when Finish is called due to compound glyphs.
    byte [] fbytes = GetFontBytes(); byte [] dfbytes = PdfWriter.Deflate( fbytes );
    int fontfile = w.StartObj(); w.Put( "<</Length1 " + fbytes.Length + "/Length " + dfbytes.Length + "/Filter/FlateDecode>>stream\n" );
    w.Put( dfbytes ); w.Put( "\nendstream" ); 
    w.EndObj();

    int fontdesc = w.PutObj( "<</Type/FontDescriptor"
      + "/FontName/" + Name + "/FontFile2 " + fontfile + " 0 R"
      + "/Ascent " + Unit(YMax) + "/Descent " + Unit(YMin) 
      + "/CapHeight " + Inp.CapHeight // Needs OS/2 table to work.
      + "/FontBBox[" + Unit(XMin) + " " + Unit(YMin) + " " + Unit(XMax) + " " + Unit(YMax) + "]"
      + "/ItalicAngle 0/StemV 80" // Should probably come from font file somehow ( ToDo )
      + "/Flags 32" // ToDo
      + ">>" );

    int cidfont = w.PutObj ( "<</Type/Font/Subtype/CIDFontType2/BaseFont/" + Name + "/FontDescriptor " + fontdesc + " 0 R"
     // + "/CIDToGIDMap/Identity" // Not required as this is default value.
     + "/CIDSystemInfo<</Registry(Adobe)/Ordering(UCS)/Supplement 0>>" // If this is not included, Acrobat will generate an error message.
     + "/W " + GetAdvanceWidths( userglyph )
     + ">>" );

    // ToUnicode map.
    int [] codes = new int[ userglyph ];
    foreach ( Generic.KeyValuePair<int,int> p in Lookup ) codes[ XG(p.Value) ] = p.Key;
    int touni = Pdf.PdfCmap.Write( w, codes );

    w.StartObj( Obj );
    w.Put( "<</Type/Font/Subtype/Type0/BaseFont/" + Name 
      + "/Encoding/Identity-H/DescendantFonts[" + cidfont + " 0 R]" + "/ToUnicode " + touni + " 0 R" + ">>" );
    w.EndObj();
  }

  private String GetAdvanceWidths( int userglyph )
  {
    System.Text.StringBuilder s = new System.Text.StringBuilder();
    s.Append( "[ 0 [" );
    for ( int i = 0; i < userglyph; i += 1 )
    {
      TrueType.WidthInfo w; Inp.GetGlyphWidth( GList[i], out w );
      s.Append( Unit(w.AdvanceWidth) );
      s.Append( " " );
    }
    s.Append( "]]" );
    return s.ToString();
  }

  private short XMin, XMax, YMin, YMax; // These are calculated by GetFontBytes.

  private byte[] GetFontBytes() // Returns the TrueType subset file as an array of bytes.
  {
    /* Tables required in theory ( those marked '?' may not actually be needed in PDF, maxp IS needed ).
       'cmap' character to glyph mapping (?)
       'glyf' glyph data
       'head' font header
       'hmtx' horizontal metrics
       'hhea' horizontal header
       'loca' index to location
       'maxp' maximum profile
       'name' naming (?)
       'post' PostScript (?)
       'OS/2' Font validator says this is a required table (?)
    */

    TrueType.Writer tw = new TrueType.Writer( 7 ); // glyf, head, hmtx, hhea, loca, maxp

    Generic.List<uint> locations = new Generic.List<uint>();

    // Summary values for 'head' 'hhea' and 'maxp' tables calculated as 'glyp' table is written.
    XMin = 0x7fff; XMin = 0x7fff; XMax = - 0x8000; YMax = - 0x8000;
    int maxContours=0, maxPoints=0, 
      maxComponentPoints=0, maxComponentContours=0, maxComponentDepth=0, maxComponentElements=0,
      xMaxExtent = 0, minRightSideBearing = 0x7fff;
    int advanceWidthMax = -0x8000, minLeftSideBearing=0x7fff; 

    // 'glyf' table
    tw.BeginTable();
    for ( int gi=0; gi < GList.Count; gi += 1 )
    {
      int sgi = GList[gi];

      TrueType.Glyph g; Inp.ReadGlyph( sgi, false, out g );
      locations.Add( tw.Offset() );

      TrueType.WidthInfo w; Inp.GetGlyphWidth( sgi, out w );
      if ( w.AdvanceWidth > advanceWidthMax ) advanceWidthMax = w.AdvanceWidth;
      if ( w.LeftSideBearing < minLeftSideBearing ) minLeftSideBearing = w.LeftSideBearing;
      if ( g.XMin < XMin ) XMin = g.XMin;
      if ( g.YMin < YMin ) YMin = g.YMin;
      if ( g.XMax > XMax ) XMax = g.XMax;
      if ( g.YMax > YMax ) YMax = g.YMax;
      int extent = w.LeftSideBearing + ( g.XMax - g.XMin );
      if ( extent > xMaxExtent ) xMaxExtent = extent;
      int rsb = w.AdvanceWidth - w.LeftSideBearing - ( g.XMax - g.XMin );
      if ( rsb < minRightSideBearing ) minRightSideBearing = rsb;

      if ( g.Contours != 0 )
      {
        if ( g.Contours >= 0 )
        {
          if ( g.Contours > maxContours ) maxContours = g.Contours;
          if ( g.Points > maxPoints ) maxPoints = g.Points;

          if ( KeepInstructions )
            tw.Write( Inp.Data, g.Pos, g.Len );
          else
          {
            int off = 10 + 2*g.Contours; /* Contours .. EndPoints */
            tw.Write( Inp.Data, g.Pos, off );
            tw.Put16(0);
            off += 2 + g.InstructionLen;
            tw.Write( Inp.Data, g.Pos+off, g.Len - off );
          }
        }
        else // Compound glyph
        {
          tw.Write( Inp.Data, g.Pos, 10 );
          for ( int i=0; i<g.Components.Count; i+=1 )
          {
            TrueType.Component c = g.Components[i];
            c.GlyphIx = (uint)XG( (int)c.GlyphIx );
            tw.Put( c );   
          }
            
          TrueType.GlyphStats gs; Inp.GetGlyphStats( sgi, out gs );
          if ( gs.Points > maxComponentPoints ) maxComponentPoints = gs.Points;
          if ( gs.Contours > maxComponentContours ) maxComponentContours = gs.Contours;
          if ( gs.ComponentDepth > maxComponentDepth ) maxComponentDepth = gs.ComponentDepth;
          // Maximum number of components referenced at “top level” for any composite glyph.
          if ( g.Components.Count > maxComponentElements ) maxComponentElements = g.Components.Count; // Not sure what "top level" means, maybe just not recursive calc?
        }
      }
      tw.Pad(4);
    }
    locations.Add( tw.Offset() ); // Additional entry so length of final glyph is represented in locations.
    tw.EndTable( TrueType.Tid.glyf );

    // 'head' table
    tw.BeginTable();
    tw.Put32( 0x00010000 ); // Version
    tw.Put32( 0 ); // fontRevision
    tw.Put32( 0 ); // checkSumAdjustment
    tw.Put32( 0x5F0F3CF5 ); // magic number
    tw.Put16( 0 ); // flags
    tw.Put16( Inp.UnitsPerEm ); // unitsPerEm
    tw.Put64( 0 ); // created
    tw.Put64( 0 ); // modified
    unchecked
    {
      tw.Put16( (ushort) XMin );
      tw.Put16( (ushort) YMin );
      tw.Put16( (ushort) XMax );
      tw.Put16( (ushort) YMax );
    }
    tw.Put16( 0 ); // macStyle
    tw.Put16( 7 ); // lowestRecPPEM
    tw.Put16( 2 ); // fontDirectionHint
    tw.Put16( 1 ); // indexToLocFormat
    tw.Put16( 0 ); // glyphDataFormat
    tw.EndTable ( TrueType.Tid.head );

    // 'hmtx' horizontal metrics
    tw.BeginTable();
    foreach ( int gi in GList )
    {
       TrueType.WidthInfo w; Inp.GetGlyphWidth( gi, out w );
       unchecked
       {
         tw.Put16( w.AdvanceWidth );
         tw.Put16( (uint) w.LeftSideBearing );
       }
    }
    tw.EndTable( TrueType.Tid.hmtx );

    // 'hhea' horizontal header
    tw.BeginTable();
    unchecked
    {
      tw.Put32( 0x00010000 ); // Fixed    version 0x00010000 (1.0)
      tw.Put16( (uint) YMax ); // FWord     ascent  Distance from baseline of highest ascender
      tw.Put16( (uint) YMin ); // FWord     descent Distance from baseline of lowest descender
      tw.Put16( Inp.LineGap ); // FWord     lineGap typographic line gap
      tw.Put16( (uint) advanceWidthMax ); // uFWord       advanceWidthMax must be consistent with horizontal metrics
      tw.Put16( (uint) minLeftSideBearing ); // FWord     minLeftSideBearing      must be consistent with horizontal metrics
      tw.Put16( (uint) minRightSideBearing ); // FWord     minRightSideBearing     must be consistent with horizontal metrics
      tw.Put16( (uint) xMaxExtent ); // FWord     xMaxExtent      max(lsb + (xMax-xMin))
      tw.Put16( 1 ); // int16     caretSlopeRise  used to calculate the slope of the caret (rise/run) set to 1 for vertical caret
      tw.Put16( 0 ); // int16     caretSlopeRun   0 for vertical
      tw.Put16( 0 ); // FWord     caretOffset     set value to 0 for non-slanted fonts
      tw.Put16( 0 ); // int16     reserved        set value to 0
      tw.Put16( 0 ); // int16     reserved        set value to 0
      tw.Put16( 0 ); // int16     reserved        set value to 0
      tw.Put16( 0 ); // int16     reserved        set value to 0
      tw.Put16( 0 ); // int16     metricDataFormat 0 for current format
      tw.Put16( (uint)GList.Count ); // uint16    numOfLongHorMetrics number of advance widths in metrics table
    }
    tw.EndTable( TrueType.Tid.hhea );

    // 'loca' table ( glyph locations )
    tw.BeginTable();
    foreach ( uint loc in locations ) tw.Put32(loc);
    tw.EndTable( TrueType.Tid.loca );

    // 'maxp' maximum profile table
    tw.BeginTable();
    tw.Put32( 0x00010000 ); // version
    tw.Put16((uint)GList.Count); // numGlyphs the number of glyphs in the font
    tw.Put16((uint)maxPoints); // maxPoints       points in non-compound glyph
    tw.Put16((uint)maxContours); // maxContours     contours in non-compound glyph
    tw.Put16((uint)maxComponentPoints); // maxComponentPoints      points in compound glyph ( todo )
    tw.Put16((uint)maxComponentContours); // maxComponentContours    contours in compound glyph ( todo )
    tw.Put16(2); // maxZones        set to 2
    tw.Put16(0); // maxTwilightPoints       points used in Twilight Zone (Z0)
    tw.Put16(0); // maxStorage      number of Storage Area locations
    tw.Put16(0); // maxFunctionDefs number of FDEFs
    tw.Put16(0); // maxInstructionDefs      number of IDEFs
    tw.Put16(0); // maxStackElements        maximum stack depth
    tw.Put16(0); // maxSizeOfInstructions   byte count for glyph instructions
    tw.Put16((uint)maxComponentElements); // maxComponentElements    number of glyphs referenced at top level
    tw.Put16((uint)maxComponentDepth); // maxComponentDepth levels of recursion, set to 0 if font has only simple glyphs
    tw.EndTable( TrueType.Tid.maxp );

    // 'cmap' table : doesn't seem to be needed by PDF ( PDF has own ToUnicode representation ), may be useful when testing.
    // WriteCmap( tw );

    // 'name' naming
    /*
    tw.BeginTable();
    tw.Put16(0); // UInt16 format  Format selector. Set to 0.
    tw.Put16(0); // UInt16 count   The number of nameRecords in this name table.
    tw.Put16(0); // UInt16 stringOffset    Offset in bytes to the beginning of the name character strings.
    // NameRecord  nameRecord[count]       The name records array.
    // variable name character strings The character strings of the names. Note that these are not necessarily ASCII!
    tw.EndTable( Tid.name );
    */
    
    // 'post' PostScript   
    /*
    tw.BeginTable();
    tw.Put32(0x00030000); // Fixed      format  Format of this table
    tw.Put32(0); // Fixed       italicAngle     Italic angle in degrees
    tw.Put16(0); // FWord       underlinePosition       Underline position
    tw.Put16(0); // FWord       underlineThickness      Underline thickness
    tw.Put32(0); // uint32      isFixedPitch    Font is monospaced; set to 1 if the font is monospaced and 0 otherwise (N.B., to maintain compatibility with older versions of the TrueType spec, accept any non-zero value as meaning that the font is monospaced)
    tw.Put32(0); // uint32      minMemType42    Minimum memory usage when a TrueType font is downloaded as a Type 42 font
    tw.Put32(0); // uint32      maxMemType42    Maximum memory usage when a TrueType font is downloaded as a Type 42 font
    tw.Put32(0); // uint32      minMemType1     Minimum memory usage when a TrueType font is downloaded as a Type 1 font
    tw.Put32(0); // uint32      maxMemType1     Maximum memory usage when a TrueType font is downloaded as a Type 1 font
    tw.EndTable( Tid.post );
    */

    tw.Finish();
    byte [] result = tw.ToArray();
    // Util.WriteFile( "test.ttf", result ); // ( for debugging )
    return result;
  }

  /*
  void WriteCmap( TtfWriter tw ) // Since PDF represents CMaps at a higher level (ToUnicode), this is not logically needed.
  {
   int nc = Lookup.Count;
   SortedList<int,int> s = new SortedList<int,int>();
   foreach ( KeyValuePair<int,int> p in Lookup ) s[ p.Key ] = p.Value;

   tw.BeginTable();
   tw.Put16( 0 ); // version
   tw.Put16( 1 ); // numberSubtables
   // Question is which format to use...
   tw.Put16( 0 ); // platformID
   tw.Put16( 10 ); // platformSpecificID
   tw.Put32( 12 ); // offset of subtable

   tw.Put16( 12 ); // format 12
   tw.Put16( 0 );
   tw.Put32( (uint)(16 + nc*12) ); // length of subtable
   tw.Put32( 0 ); // language
   tw.Put32( (uint) nc ); // number of groups ( each group maps one char )

   foreach ( KeyValuePair<int,int> q in s )
   {
     tw.Put32( (uint) q.Key ); // startCharCode
     tw.Put32( (uint) q.Key ); // endCharCode
     tw.Put32( (uint) Xlat[q.Value] ); // startGlyphCode
   }   
   
   tw.EndTable( Tid.cmap );
  } // writeCmap
  */  

  /* Test functions */

  /*
  void CopyTable( TtfWriter tw, uint tid )
  {
    if ( Inp.GotoTable( tid ) )
    {      
      tw.BeginTable();
      tw.Write( Inp.Data, (int)Inp.Ix, (int)(Inp.IxLimit-Inp.Ix) );
      tw.EndTable( tid );
    }
  }

  public void CopyTest() // For testing TtfReader and TtfWriter
  {
    TtfWriter tw = new TtfWriter( Inp.Tables.Count );
    foreach( KeyValuePair<uint,TtfReader.Table> p in Inp.Tables )
    {
      uint name = p.Key;
      // if ( name ==  Tid.cmap || name == Tid.glyf || name == Tid.head || name == Tid.hhea || name == Tid.hmtx || name == Tid.loca
      //  || name == Tid.maxp || name == Tid.name || name == Tid.post )

      CopyTable( tw, p.Key );
    }
    tw.Finish();
    Util.WriteFile( "copytest.ttf", tw.ToArray() );
  }   
  */

} // End class TrueTypeFont

} // namespace

