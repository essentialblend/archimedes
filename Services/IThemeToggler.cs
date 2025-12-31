using System;
using System.Collections.Generic;
using System.Text;

namespace archimedes
{
    // Contract: anything that can toggle the app theme implements this.
    public interface IThemeToggler
    {
        void ToggleTheme();
    }
}

