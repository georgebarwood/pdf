using String = System.String;

namespace Pdf
{

class PdfWriter2 : PdfWriter 
// Extends PdfWriter to give each page a border, background image and page number. 
// Also has function to write HTML to Pdf.
{
  // Default settings, can be modified.
  public PdfImage BackgroundImage = null;
  public int BorderPadding = 10;
  public bool NumberPages = true;

  public override void StartPage()
  {
    CP.DrawImage( BackgroundImage, 50, 255, 0.65f ); // Add background image
    
    if ( BorderPadding > 0 ) // Draw a border box
    {
      int BP = BorderPadding;
      float x0 = CP.Layout.MarginLeft - BP, 
         x1 = CP.Layout.Width - CP.Layout.MarginRight + BP, 
         y0 = CP.Layout.MarginBottom - BP, 
         y1 = CP.Layout.Height - CP.Layout.MarginTop + BP;
      CP.Rect(x0, y0, x1-x0, y1-y0);
    }
    CP.TSW( "\nq" ); // Save state
  }

  public override void FinishPage()
  {
    InitFont( Fonts[0] );
    CP.ClearState();
    CP.TSW( "\nQ" ); // Restore state
    if ( NumberPages )
    {
      CP.Goto( CP.Layout.MarginLeft, CP.Layout.MarginBottom - BorderPadding - 15 );
      CP.SetFont( Fonts[0], 10 ); 
      CP.Txt( "Page " + CP.Number + " of " + Pages.Count );
    }
  }

  public void Html( string s )
  {
    // Function to process simple HTML/XML. Supported tags : p, br, b, i, sup, sub. Other tags currently have no effect.
    // Sample input : "<p>Hello <b>there</b> from <i>Para</i> 1!</p><p>c<sup>2</sup> = a<sup>2</sup> + b<sup>2</sup><p>Para 3</p>"
    // Closing tags can be omitted if there is an enclosing tag that implies the closure, e.g. "<b><i>Hello</b> there"
    // The character '<' need not be escaped if next char is not a letter or '/'
    // &lt; and &amp; allow < and & to be escaped if necessary.
    Paracount = 0;
    Html(s, 0, null);
  }  

  int Paracount; // For suppressing space prior to first paragraph

  int Html( String s, int i, String endtag )
  {
    int n = s.Length;
    int plain = i; // Start of plain (not within a tag) text.
    while ( i < n-2 )
    {
      char c = s[i]; 
      i += 1;
      if ( c == '&' && i < n-2 ) // & char literals
      {
        Txt( s, plain, i-1 );
        if ( s[i] == 'l' && s[i+1] == 't' && s[i+2] == ';' )
        {
          Txt("<"); i += 3;
        }
        else if ( i < n-3 && s[i] == 'a' && s[i+1] == 'm' && s[i+2] == 'p' && s[i+3] == ';' )
        {
          Txt("&"); i += 4;
        }
        else Txt( "&" );
        plain = i;
      }      
      else if ( c == '<' ) // HTML tag
      {
        char t = s[i]; // First char of the tag   
        if ( t == '/' || ( t >= 'a' && t <='z' ) || ( t >= 'A' && t <= 'Z' ) )
        {    
          Txt( s, plain, i-1 );
          i += 1;
          int tagstart=i-1, tagend = -1;
          while ( i < n ) // Loop to find end of the tag
          {
            c = s[i];
            if ( c == ' ' ) tagend = i;
            i += 1;
            if ( c == '>' ) break; 
          }
          if (tagend < 0) tagend = i-1;
          if ( t == '/' ) tagstart = tagstart + 1;

          String tag = tagend>tagstart ? s.Substring(tagstart,tagend-tagstart) : "";

          if ( t == '/' ) return tag == endtag ? i : tagstart-2;

          if ( tag == "br" || tag == "br/" ) NewLine();
          else 
          {
            int Save = 0; PdfFont SaveF = null;
            if ( tag == "p" ) { Paracount+=1; if ( Paracount > 1 ){ NewLine(); NewLine();} }
            else if ( tag == "b" ) { SaveF = Font; PdfFont nf = (SaveF==Fonts[2]) ? Fonts[3] : Fonts[1]; SetFont( nf, FontSize ); }
            else if ( tag == "i" ) { SaveF = Font; PdfFont nf = (SaveF==Fonts[1]) ? Fonts[3] : Fonts[2]; SetFont( nf, FontSize );  }
            else if ( tag == "sup" ) { Save = Super; SetSuper(FontSize/2); }
            else if ( tag == "sub" ) { Save = Super; SetSuper(-FontSize/2); }
            i = Html( s, i, tag );
            if ( tag == "b" || tag == "i" ) SetFont( SaveF, FontSize );
            else if ( tag == "sup" || tag == "sub" ) { SetSuper(Save); }
          }
          plain = i;
        }
      }
    } 
    i = n;
    Txt( s, plain, i );
    return i;
  }
}  // End class Writer2

} // namespace
