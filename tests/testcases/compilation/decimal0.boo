"""
0
1
-1

0
1
-1

1.1

0

False
True
"""
print decimal.Zero
print decimal.One
print decimal.MinusOne
print
print 0dec
print 1dec
print -1dec
print
print 1.1dec
print
print 0DEC
print

	# that was boring.
	# let's show off!

# it's double the precision, but still can't add ten numbers:
print((0.1+0.1+0.1+0.1+0.1+0.1+0.1+0.1+0.1+0.1)==1)  # False

# decimal does the job which is why business apps like it:
print((0.1dec+0.1dec+0.1dec+0.1dec+0.1dec+0.1dec+0.1dec+0.1dec+0.1dec+0.1dec)==1)  # True