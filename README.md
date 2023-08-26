# stride-gamestudio-nuget
A neat fact that strides editor can run only using nuget and no installs.

# The Awesome!
The most interesting thing about this example is we now have very easy access to the plugin service without having to modify the Stride source code.

![image](https://user-images.githubusercontent.com/73259914/232235882-75f97278-f038-497b-b79a-0108932772aa.png)

Those are 2 services that are in Stride by default but I assume nothing stops us from injecting our own plugins.

# The Problems
One issue I found is there seems to be a runtime error regarding fonts although I havent noticed what it actually breaks. 

![image](https://user-images.githubusercontent.com/73259914/232236055-da18fc85-21ea-43ba-b5e6-2abd68206c38.png)

Another issue, that doesnt really matter but could explain some future/current problems that are found, is the icon for gamestudio is the default windows app icon. Clearly with this way of using gamestudio some resources have not been included or loaded.
