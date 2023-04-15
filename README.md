# stride-gamestudio-nuget
A neat fact that strides editor can run only using nuget and no installs.


The most interesting thing about this exaple is we now have very easy access to the plugin service without having to midify the Stride source code.
![image](https://user-images.githubusercontent.com/73259914/232235882-75f97278-f038-497b-b79a-0108932772aa.png)
Those are 2 services that are in Stride by default but I assume nothing stops us from injecting our own plugins.

# Issues
One issue I found is there seems to be a runtime error rgarding fonts although I havent noticed what it actually breaks. 
![image](https://user-images.githubusercontent.com/73259914/232236055-da18fc85-21ea-43ba-b5e6-2abd68206c38.png)

Another issue that doesnt really matter but could explain some future problems that are found is the icon for gamestudio is the default windows app icon. Clearly with this way of using gamestudio some resources have not been included.
