using Generic = System.Collections.Generic;
using Exception = System.Exception;

namespace TrueType {

/* 

Code to help read and write TrueType ( '.ttf' ) files. Main classes are Reader and Writer.

ToDo: consider convention where glyph 0 is "error" glyph. Support for TTC?

References: 
  https://en.wikipedia.org/wiki/B%C3%A9zier_curve
  https://www.freetype.org/freetype2/docs/tutorial/step2.html#section-1

"Hinting" involves interpreted programs that adjust the conversion of a glyph outline to a pixel grid.
For now, hints are discarded.

The font outline is specified using Bezier curves. 
For TrueType three points P0, P1, P2 are used to specify each curve segment ( a quadratic Bezier curve ).
The curve is expressed as B(t) = (1-t)*((1-t)*P0 + t*P1) + t*((1-t)*P1+t*P2) where t varies for 0 to 1.

A font is designed in integer units. UnitsPerEm specifies the size of the font in these internal units.
( The size of the font, the "Em" is up to the font designer, but is usually the minimum line height for the font ).
Typical values for UnitsPerEm are 1000, 1024 or 2048.
WidthInfo returns character width in the internal units.
AdvanceWidth is the distance the "Pen" advances while writing a particular character ( including some space ).
PDF units are "pt" which is 1/72 of an inch.

Note: a quadratic curve Q0, Q1, Q2 can be expressed as a cubic curve C0, C1, C2, C3 using

C0 = Q0, C1 = (Q0/3 + 2*Q1/3), C2 = (2*Q1/3 + Q2/3), C3 = Q2 

There are complications relating to how glyphs from un-related languages / fonts should be drawn, and vertical writing
( see https://en.wikipedia.org/wiki/Horizontal_and_vertical_writing_in_East_Asian_scripts ).

Websites:
http://stevehanov.ca/blog/index.php?id=143
https://fontdrop.info/
https://opentype.js.org/font-inspector.html
https://developer.apple.com/fonts/TrueType-Reference-Manual/
https://docs.microsoft.com/en-gb/typography/opentype/spec/ttochap1
https://www.freetype.org/freetype2/docs/glyphs/glyphs-3.html

https://www.pdf-online.com/osa/validate.aspx
https://freetoolonline.com/preflight-pdf.html
https://scripts.sil.org/cms/scripts/page.php?site_id=nrsi&id=CatTypeDesignResources#754b7682

http://pages.ucsd.edu/~dkjordan/chin/unitestuni.html
https://mjn.host.cs.st-andrews.ac.uk/egyptian/fonts/newgardiner.html

*/

struct Glyph
{
  public int Contours;
  public short XMin, YMin, XMax, YMax;
  public ushort Points;
  public int InstructionLen;
  public int Pos, Len;
  // Rest are not assigned by Readglyph if getPoints is false.
  public ushort [] EndPoints;
  public byte [] Flags;
  public short [] X, Y;  
  public Generic.List<Component> Components; // signalled by Contours = -1.
}

struct Component { public uint GlyphIx, Flags, A1, A2, S1, S2, S3, S4; }

struct WidthInfo { public ushort AdvanceWidth; public short LeftSideBearing; }

struct GlyphStats { public int Points, Contours, ComponentDepth; }

class Reader // class to decode a TrueType file.
{
  // Public interface
  // public Reader( byte [] data ) // data is contents of a .ttf file
  // public int FindGlyph( int c ) // c is unicode value, returns glyph index (gi), or -1 if not found (?).
  // public void GetGlyphWidth( int gi, out WidthInfo result )
  // public void GetGlyphStats( int gi, out GlyphStats result )
  // public void ReadGlyph( int gi, bool getPoints, out Glyph result )
  
  public uint UnitsPerEm; // from 'head'
  public uint Ascent, Descent, LineGap; // from 'hhea'
  public int CapHeight; // from 'OS/2'

  public byte [] Data;

  uint Ix, IxLimit, CmapLimit;
  int IndexToLocFormat;  
  uint NumOfLongHorMetrics;

  Generic.List<uint> Cmap = new Generic.List<uint>();
  struct Table {  public uint Offset, Length; public Table( uint off, uint len ) { Offset=off; Length=len; } }
  Generic.Dictionary<uint,Table> Tables = new Generic.Dictionary<uint,Table>();

  public Reader( byte [] data )
  { 
    Data = data; 
    Ix=4; IxLimit = 12; // Skip ScalerType
    uint numTables = Get16();
    Ix += 6; // Skip SearchRange, EntrySelector, RangeShift
    IxLimit += numTables * 16;
    for ( int i=0; i<numTables; i+=1 )
    {
      uint tag = Get32();
      Ix += 4; // Skip checksum
      uint off = Get32();
      uint len = Get32();
      Tables[tag] = new Table( off, len );;
    }
    GotoTable( Tid.head );
    Ix += 18; // Skip Version .. MagicNumber, flags = 16 */
    UnitsPerEm = Get16();
    Ix += 30;  // created, modified = 16, xMin .. fontDirectionHint = 14
    IndexToLocFormat = GetI16();   

    GotoTable(Tid.hhea);
    Ix += 4;
    Ascent = Get16();
    Descent = Get16();
    LineGap = Get16();
    Ix += 24;
    NumOfLongHorMetrics = Get16();

    if( GotoTable(Tid.OS2) )
    {
      int ver = GetI16();
      if ( ver >= 2 )
      {
        Ix += 86; /* 15 * 2 + 10 + 4*4 + 4 + 8 * 2 + 2 * 4 + 2 */
        CapHeight = GetI16();
      }
    }
    GetCmaps();
  }

  byte Get8(){ if ( Ix + 1 > IxLimit ) throw new Exception(); return Data[Ix++]; }

  uint Get32()
  {
    if ( Ix + 4 > IxLimit ) throw new Exception();
    uint result = 0;
    for ( int i=0; i<4; i+=1 ) result = (result<<8) + Data[Ix++]; 
    return result;
  }

  uint Get16()
  {
    Ix += 2;
    if ( Ix > IxLimit ) throw new Exception();
    return ( ((uint)Data[Ix-2]) << 8 ) + ( uint)Data[Ix-1];
  }

  int GetI16()
  {
    if ( Ix + 2 > IxLimit ) throw new Exception();
    int x = ( Data[Ix] << 8 ) + Data[Ix+1];
    Ix += 2;
    if ( x > 0x7fff ) x -= 0x10000;
    return x;
  }

  bool GotoTable( uint tag )
  {
    Table t;
    if ( Tables.TryGetValue( tag, out t ) )
    {
      Ix = t.Offset;
      IxLimit = t.Offset + t.Length;
      return true;
    }
    return false;
  }

  public int FindGlyph( int uc )
  {
    uint c = (uint)uc;
    foreach ( uint ix in Cmap )
    {
      Ix = ix; IxLimit = CmapLimit;
      uint format = Get16();
      switch (format)
      {
        case 6:  
        {
          Ix += 4;  uint firstCode = Get16(); uint entryCount = Get16();
          if ( c >= firstCode && c <= firstCode + entryCount ) { Ix += 2 * ( c - firstCode ); return (int)Get16(); }
        }
        break;  
        case 4:
        {
          Ix += 4;  // skip length and language
          uint segCountX2 = Get16();
          Ix += 6; // skip searchRange .. rangeShift        
          uint s = 0, e = segCountX2/2-1, bix = Ix;
          while ( true )
          {
            uint mid = (s+e)/2; Ix = bix + mid*2; uint end = Get16();
            if ( c <= end ) 
            { 
              e = mid; 
              if ( s == e ) 
              {
                Ix += segCountX2; uint start = Get16();
                if ( start <= c )
                {
                  Ix += segCountX2-2; uint idDelta = Get16();
                  Ix += segCountX2-2; uint idOffset = Get16(); 
                  if ( idOffset == 0  ) return (int)( ( c + idDelta ) & 0xffff );
                  else { Ix -= 2; Ix += idOffset + 2 * ( c - start ); return (int)Get16(); }
                }
                break; 
              }
            }
            else { s = mid+1; if ( s > e ) break; }
          }
        }
        break;
        case 12: // 32 bit version of case 4 with simpler data layout.
        {
          Ix += 10; // Skip half version, length, language
          uint s = 0, e = Get32() - 1, bix = Ix;
          while ( true )
          {
            uint mid = (s+e)/2; Ix = bix + mid*12+4; uint end = Get32();
            if ( c <= end )
            {
              e = mid;
              if ( s == e )
              {
                Ix -= 8; uint start = Get32();
                if ( start <= c ) { Ix += 4; uint gi = Get32(); return (int)( gi + ( c - start ) );  }
                break;
              }
            }
            else { s = mid+1; if ( s > e ) break; }
          }
        }
        break;
      } // switch
    } // foreach loop
    return -1;
  }

  void GetCmaps()
  {
    GotoTable(Tid.cmap); 
    CmapLimit = IxLimit;
    uint cmapIx=Ix; 
    Ix += 2; // Skip version
    uint numberSubtables = Get16();
    for ( int i=0; i<numberSubtables; i+=1 )
    {
      uint platformID = Get16();
      uint platformSpecificID = Get16();
      uint offset = Get32();
      if ( platformID == 0 || platformID == 3 && ( platformSpecificID == 1 || platformSpecificID == 10 ) ) // Is it a Unicode cmap?
      { Cmap.Add( cmapIx + offset ); }
    }
  }

  public void GetGlyphWidth( int gi, out WidthInfo result )
  {
     GotoTable(Tid.hmtx);
     if ( gi < NumOfLongHorMetrics )
     {
       Ix += (uint)gi*4;
       result.AdvanceWidth = (ushort)Get16();
       result.LeftSideBearing = (short)GetI16();
     }
     else
     {
       Ix += ( NumOfLongHorMetrics - 1 ) *4;
       result.AdvanceWidth = (ushort)Get16();
       Ix += ( (uint)gi - NumOfLongHorMetrics ) * 2 + 2;
       result.LeftSideBearing = (short)GetI16();
    }
  }

  public void GetGlyphStats( int gi, out GlyphStats result )
  {
    uint SaveIx = Ix, SaveIxLimit = IxLimit; // Save IO position
    GotoTable( Tid.loca );
    uint offset, next; 
    if ( IndexToLocFormat == 0 ){ Ix += (uint)gi * 2; offset = 2*Get16(); next = 2*Get16(); } // Note the offsets are multiplied by 2 in the short format!
    else  { Ix += (uint)gi*4; offset = Get32(); next = Get32(); }

    GotoTable( Tid.glyf );
    Ix += offset;

    result = new GlyphStats();
    int contours = next == offset ? 0 : GetI16();
    if ( contours >= 0 )
    {
      result.Contours = contours;
      if ( contours > 0 ) 
      {
        Ix += (uint) ( 8 + 2*(contours-1) );
        result.Points = 1 + GetI16();
      }
    }
    else
    {
      Ix += 8;
      while ( 1 == 1 )
      {
        uint flags = Get16();
        int glyphIndex = (int)Get16();
        uint a1, a2, s1=0, s2=0, s3=0, s4=0;
        if ( (flags & 1 ) != 0 ) { a1 = Get16(); a2 = Get16(); } else { a1 = Get8(); a2 = Get8(); } 
        if ( (flags & 8 ) != 0 ) s1 = Get16(); 
        else if ( (flags & 0x40 ) != 0 ) { s1 = Get16(); s2 = Get16(); }
        else if ( (flags & 0x80 ) != 0 ) { s1 = Get16(); s2 = Get16(); s3 = Get16(); s4 = Get16(); }

        GlyphStats cs; GetGlyphStats( glyphIndex, out cs );
        result.Points += cs.Points;
        result.Contours += cs.Contours;
        result.ComponentDepth = cs.ComponentDepth + 1;

        if ( ( flags & 0x20 ) == 0 ) break;
      }
    }
    Ix = SaveIx; IxLimit = SaveIxLimit; // Restore IO position
  }

  public void ReadGlyph( int gi, bool getPoints, out Glyph g ) 
  {
    GotoTable( Tid.loca );
    uint offset, next; 
    if ( IndexToLocFormat == 0 ){ Ix += (uint)gi * 2; offset = 2*Get16(); next = 2*Get16(); } // Note the offsets are multiplied by 2 in the short format!
    else  { Ix += (uint)gi*4; offset = Get32(); next = Get32(); }    

    g = new Glyph();
    if ( next == offset ) { g.Contours = 0; g.Len = 0; } // Empty glyph ( space )
    else
    {
      GotoTable( Tid.glyf );
      IxLimit = Ix+next;
      Ix += offset;      

      g.Pos = (int)Ix;
      g.Contours = GetI16();
      g.XMin = (short)GetI16();
      g.YMin = (short)GetI16();
      g.XMax = (short)GetI16();
      g.YMax = (short)GetI16();

      if ( g.Contours >= 0 ) // Simple glyph
      {
        ushort [] endPtsOfContours = new ushort[g.Contours]; // Shouldn't allocate this is !getPoints, but currently used to calculate # points.
        uint points = 0;

        if ( getPoints )
        {
          for ( int i = 0; i < g.Contours; i += 1 ) 
          { 
            uint ep = Get16();
            endPtsOfContours[ i ] = (ushort)ep;      
          }
          g.EndPoints = endPtsOfContours;
          points = (uint)g.EndPoints[g.Contours-1] + 1;
        }
        else { Ix += (uint)(2*(g.Contours-1)); points = 1+Get16(); }

        g.Points = (ushort)points;
        uint instructionLength = Get16();
        g.InstructionLen = (int)instructionLength;

        if (!getPoints) // Usually we don't need to parse the points.
        {
          Ix = IxLimit;
        }
        else
        { 
          Ix += instructionLength;
          byte [] flags = new byte[points];

          byte flag = 0, rep =0;
          for ( int i = 0; i < points; i += 1 )
          {
            if ( rep > 0 ) rep -= 1;
            else { flag = Get8(); if ( ( flag & 8 ) != 0 ) rep = Get8(); }
            flags[i] = flag;
          }
          short [] xs = new short[points];
          int x = 0;
          for ( int i = 0; i < points; i += 1 )
          {
            flag = flags[i];
            if ( ( flag & 2 ) != 0 ) { byte b = Get8(); if ( ( flag & 16 ) != 0 ) x += b; else x -= b; }
            else if ( ( flag & 16 ) == 0 ) x += GetI16();
            xs[i] = (short)x;
          }
          short [] ys = new short[points];
          int y = 0;
          for ( int i = 0; i < points; i += 1 )
          {
            flag = flags[i];
            if ( ( flag & 4 ) != 0 ) { byte b = Get8(); if ( ( flag & 32 ) != 0 )  y += b; else y -= b; }
            else if ( ( flag & 32 ) == 0 ) y += GetI16(); 
            ys[i] = (short)y;
          }
          g.Flags = flags;
          g.X = xs;
          g.Y = ys; 
        }  
      }
      else // Compound glyph
      {
        Generic.List<Component> components = new Generic.List<Component>();
        while ( 1 == 1 )
        {
          uint flags = Get16();
          uint glyphIndex = Get16();
          uint a1, a2, s1=0, s2=0, s3=0, s4=0;
          if ( (flags & 1 ) != 0 ) { a1 = Get16(); a2 = Get16(); } else { a1 = Get8(); a2 = Get8(); } 
          if ( (flags & 8 ) != 0 ) s1 = Get16(); 
          else if ( (flags & 0x40 ) != 0 ) { s1 = Get16(); s2 = Get16(); }
          else if ( (flags & 0x80 ) != 0 ) { s1 = Get16(); s2 = Get16(); s3 = Get16(); s4 = Get16(); }

          Component nc; nc.GlyphIx = glyphIndex; nc.Flags = flags; nc.A1 = a1; nc.A2 = a2; nc.S1=s1; nc.S2=s2; nc.S3=s3; nc.S4 = s4;
          components.Add( nc );
         
          if ( ( flags & 0x20 ) == 0 ) break;
        }
        g.Components = components;
      }
      g.Len=(int)(Ix-g.Pos);
    }
  }

/* For debugging / testing */

/*
  static String DecodeTag( uint tag )
  {
    String result = "";
    for ( int i=0; i<4; i+= 1 ) result = result + (char) (byte) ( tag >> (24-i*8) );
    return result;
  }

  public void InspectTable( Pdf.Writer w, uint tid )
  {
    w.NewLine(); w.NewLine();
    w.Txt( "\nTable " + DecodeTag(tid) );
    GotoTable( tid );
    uint offset = 0;
    while ( Ix < IxLimit )
    {
      if ( offset % 16 == 0 ) 
      {
        w.Txt( "\n" + offset.ToString("X4") + ":" );
      }
      w.Txt( Get8().ToString("X2") + " " );
      offset += 1;
    }
  }    

  public void Inspect( Pdf.Writer w )
  {
    foreach ( KeyValuePair<uint,Table> p in Tables )
    {
      w.Txt( "\n table " + DecodeTag(p.Key) + " offset=" + p.Value.Offset + " length=0x" + p.Value.Length.ToString("X") );
    }   
    InspectTable( w, Tid.hmtx ); 
    InspectTable( w, Tid.loca );   
    InspectTable( w, Tid.glyf );     
  }
*/

} // End class Reader

/////////////////////////////////////////////////////////////////////////////////////////////////

public class MemStream : System.IO.MemoryStream // Helper class for Writer
{
  public void Put64( ulong x )
  {
    Put32( (uint)(x >> 32) );
    Put32( (uint) x );
  }

  public void Put32( uint x )
  {
    unchecked
    {
      WriteByte( (byte)( x >> 24 ) );
      WriteByte( (byte)( x >> 16 ) );
      WriteByte( (byte)( x >> 8 ) );
      WriteByte( (byte)( x  ) );
    }
  }

  public void Put16( uint x )
  {
    unchecked
    {
      WriteByte( (byte)( x >> 8 ) );
      WriteByte( (byte)( x ) );
    }
  }

  public void Pad( uint n ) // pad to n-byte boundary
  {
    uint pad = ((uint)Position) & ( n-1 ); // for e.g. n=4, pad = 0,1,2,3
    pad = ( n - pad ) & ( n-1 );  // 0, 3, 2, 1
    while ( pad > 0 ) { WriteByte( 0 ); pad -= 1; }
  }

  public uint Get32()
  {
    uint result = 0;
    for ( int i=0; i<4; i+=1 ) result = ((uint)( result << 8 )) + (uint)ReadByte();
    return result;
  }

  public void Seek( long x )
  {
    Seek( x, System.IO.SeekOrigin.Begin );
  }
} // end class MemStream

class Writer : MemStream // Helper class for writing TrueType files.
{
  int MaxTable;
  int TableCount = 0;
  long TablePos;
  uint SumChk = 0;
  long CheckSumAdjPos;

  struct DirEntry { public uint Chk, Off, Len; }    

  Generic.SortedList<uint,DirEntry> Dir = new Generic.SortedList<uint,DirEntry>();

  public Writer( int maxtable )
  { 
    MaxTable = maxtable;
    TablePos = 12 + MaxTable * 16 + 4; // align on 8 byte boundary
  }

  public long BeginTable()
  {
    if ( TableCount == MaxTable ) throw new Exception();
    Seek( TablePos );
    return TablePos;
  }

  public uint Offset() { return (uint)(Position - TablePos);  }

  public void Put( Component c )
  {
    Put16( c.Flags & 0xfeff ); // Clear the instructions bit
    Put16( c.GlyphIx );
    if ( ( c.Flags & 1 ) != 0 ) { Put16(c.A1); Put16(c.A2); } else { WriteByte((byte)c.A1); WriteByte((byte)c.A2); } 
    if ( ( c.Flags & 8 ) != 0 ) Put16( c.S1 );
    else if ( (c.Flags & 0x40 ) != 0 ) { Put16( c.S1 ); Put16( c.S2 ); }
    else if ( (c.Flags & 0x80 ) != 0 ) { Put16( c.S1 ); Put16( c.S2 ); Put16(c.S3); Put16(c.S4); }
  }

  public uint Sum( long start, int n )
  { Seek( start ); uint result = 0; while ( n-- > 0 ) unchecked { result += Get32(); } return result; }     

  public void EndTable( uint tag )
  {
    long len = Position - TablePos; 
    Pad( 8 );
    long next = Position;

    if ( tag == Tid.head ) CheckSumAdjPos = TablePos + 8;

    uint chk = Sum( TablePos, (int)((len+3)/4) );

    DirEntry de; de.Off=(uint)TablePos; de.Chk = chk; de.Len = (uint)len;
    Dir[tag] = de;

    TableCount += 1;
    TablePos = next;

    unchecked { SumChk += chk; }
  }

  public void Finish()
  {    
    int entrySelector = 1;
    int mp2 = 2; while ( mp2 * 2 <= TableCount ) { entrySelector += 1; mp2 *= 2; }

    Seek( 0 );
    Put32( 0x00010000 ); // scaler type
    Put16( (uint) TableCount );
    Put16( (uint) ( mp2 * 16 ) ); // searchRange (maximum power of 2 <= numTables)*16
    Put16( (uint) entrySelector ); // entrySelector log2(maximum power of 2 <= numTables)
    Put16( (uint) ( TableCount*16 - mp2 * 16 ) ); // rangeShift numTables*16-searchRange

    foreach ( Generic.KeyValuePair<uint,DirEntry> p in Dir )
    { 
      Put32( p.Key );
      Put32( p.Value.Chk );
      Put32( p.Value.Off );
      Put32( p.Value.Len );
    }

    //SumChk = Sum( 0, (int)((Length+3)/4) );
    unchecked
    {
      SumChk += Sum( 0, ( 12 + MaxTable * 16 + 4 ) / 4 );
      SumChk = 0xB1B0AFBA - SumChk;
    }
    Seek( CheckSumAdjPos );
    Put32( SumChk );
  }

} // end class Writer

public class Tid // TrueType table identifier values
{
  public const uint hhea = 0x68686561, head = 0x68656164, hmtx = 0x686d7478, cmap = 0x636d6170,
    glyf = 0x676c7966, maxp = 0x6d617870, loca = 0x6c6f6361, name = 0x6e616d65, post = 0x706f7374, OS2 = 0x4f532f32;
}

} // namespace
